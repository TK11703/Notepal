extern alias web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Notepal.Shared;
using NewNote = web::Notepal.Web.Components.Pages.NewNote;

namespace Notepal.Api.Tests;

#pragma warning disable BL0006 // Exercise component rendering without adding a UI test framework.

public sealed class NewNotePreparationTests
{
    [Fact]
    public async Task Selection_ShowsProgressBeforeReading_AndAddsPagesIncrementally()
    {
        using var services = new ServiceCollection().AddSingleton<IJSRuntime, NoOpJsRuntime>().BuildServiceProvider();
        await using var renderer = new PreparationRenderer(services);
        var page = new PreparationPage();
        await renderer.MountAsync(page);
        var files = Enumerable.Range(1, 6).Select(i => new DelayedFile($"page-{i}.pdf")).ToArray();
        Task? preparation = null;

        await renderer.Dispatcher.InvokeAsync(() =>
        {
            preparation = renderer.FileSelection.InvokeAsync(new InputFileChangeEventArgs(files));
        });

        var overlapping = new DelayedFile("overlapping.pdf");
        await renderer.Dispatcher.InvokeAsync(() =>
            renderer.FileSelection.InvokeAsync(new InputFileChangeEventArgs([overlapping])));
        await renderer.Dispatcher.InvokeAsync(() =>
            renderer.SaveClick());
        Assert.False(overlapping.Reading.Task.IsCompleted);

        for (var i = 0; i < files.Length; i++)
        {
            await files[i].Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await renderer.Dispatcher.InvokeAsync(() =>
            {
                Assert.Contains($"Preparing file {i + 1} of 6: {files[i].Name}", renderer.Text);
                Assert.Contains("only after you click Save note", renderer.Text);
                Assert.DoesNotContain("Nothing added yet", renderer.Text);
                Assert.True(renderer.SaveDisabled);
                Assert.True(renderer.SelectionDisabled);
                Assert.Equal(i, renderer.PageCount);
            });
            files[i].Release.SetResult();
        }

        await preparation!.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.DoesNotContain("Preparing file", renderer.Text);
            Assert.Equal(6, renderer.PageCount);
            Assert.False(renderer.SaveDisabled);
            Assert.False(renderer.SelectionDisabled);
        });
    }

    [Fact]
    public async Task ReadFailure_ClearsProgress_ShowsError_AndAllowsAnotherSelection()
    {
        using var services = new ServiceCollection().AddSingleton<IJSRuntime, NoOpJsRuntime>().BuildServiceProvider();
        await using var renderer = new PreparationRenderer(services);
        var page = new PreparationPage();
        await renderer.MountAsync(page);
        var failed = new DelayedFile("failed.pdf", fail: true);
        failed.Release.SetResult();

        await renderer.Dispatcher.InvokeAsync(() =>
            renderer.FileSelection.InvokeAsync(new InputFileChangeEventArgs([failed])));

        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Contains("Could not read 'failed.pdf'", page.Error);
            Assert.DoesNotContain("Preparing file", renderer.Text);
            Assert.False(renderer.SelectionDisabled);
            Assert.True(renderer.SaveDisabled);
        });

        var retry = new DelayedFile("retry.pdf");
        retry.Release.SetResult();
        await renderer.Dispatcher.InvokeAsync(() =>
            renderer.FileSelection.InvokeAsync(new InputFileChangeEventArgs([retry])));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Null(page.Error);
            Assert.Equal(1, renderer.PageCount);
            Assert.False(renderer.SaveDisabled);
        });
    }

    [Fact]
    public async Task TooManyFiles_ShowsLimitError_WithoutStartingPreparation()
    {
        using var services = new ServiceCollection().AddSingleton<IJSRuntime, NoOpJsRuntime>().BuildServiceProvider();
        await using var renderer = new PreparationRenderer(services);
        var page = new PreparationPage();
        await renderer.MountAsync(page);
        var files = Enumerable.Range(1, UploadLimits.MaxFilesPerNote + 1)
            .Select(i => new DelayedFile($"page-{i}.pdf")).ToArray();

        await renderer.Dispatcher.InvokeAsync(() =>
            renderer.FileSelection.InvokeAsync(new InputFileChangeEventArgs(files)));
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            Assert.Contains("at most", page.Error);
            Assert.DoesNotContain("Preparing file", renderer.Text);
            Assert.False(renderer.SelectionDisabled);
            Assert.All(files, file => Assert.False(file.Reading.Task.IsCompleted));
        });
    }

    private sealed class PreparationPage : NewNote
    {
        protected override Task OnInitializedAsync() => Task.CompletedTask;
        public string? Error => ErrorMessage;
    }

    private sealed class PreparationRenderer(IServiceProvider services)
        : Renderer(services, NullLoggerFactory.Instance)
    {
        private int root;
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        private RenderTreeFrame[] Frames
        {
            get
            {
                var frames = GetCurrentRenderTreeFrames(root);
                return frames.Array.Take(frames.Count).ToArray();
            }
        }

        public string Text => string.Concat(Frames.Select(f => f.FrameType switch
        {
            RenderTreeFrameType.Text => f.TextContent,
            RenderTreeFrameType.Markup => f.MarkupContent,
            _ => ""
        }));

        public int PageCount => Frames.Count(f => f.FrameType == RenderTreeFrameType.Element && f.ElementName == "li");
        public bool SelectionDisabled => HasDisabledAttribute("fieldset", null);
        public bool SaveDisabled => HasDisabledAttribute("button", "btn btn-primary w-100");
        public EventCallback<InputFileChangeEventArgs> FileSelection =>
            (EventCallback<InputFileChangeEventArgs>)Frames.First(f =>
                f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "OnChange").AttributeValue;
        public Func<Task> SaveClick
        {
            get
            {
                var frames = Frames;
                var button = Array.FindIndex(frames, f => f.FrameType == RenderTreeFrameType.Attribute
                    && f.AttributeName == "class" && Equals(f.AttributeValue, "btn btn-primary w-100"));
                return (Func<Task>)frames.Skip(button)
                    .First(f => f.FrameType == RenderTreeFrameType.Attribute && f.AttributeName == "onclick").AttributeValue;
            }
        }

        public Task MountAsync(PreparationPage page) => Dispatcher.InvokeAsync(async () =>
        {
            root = AssignRootComponentId(page);
            await RenderRootComponentAsync(root);
        });

        private bool HasDisabledAttribute(string element, string? cssClass)
        {
            var frames = Frames;
            for (var i = 0; i < frames.Length; i++)
            {
                if (frames[i].FrameType != RenderTreeFrameType.Element || frames[i].ElementName != element)
                    continue;
                var attributes = frames.Skip(i + 1).TakeWhile(f => f.FrameType == RenderTreeFrameType.Attribute).ToArray();
                if (cssClass is not null && !attributes.Any(f => f.AttributeName == "class" && Equals(f.AttributeValue, cssClass)))
                    continue;
                return attributes.Any(f => f.AttributeName == "disabled" && Equals(f.AttributeValue, true));
            }
            throw new InvalidOperationException($"Could not find {element}.");
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Render failed.", exception);
    }

    private sealed class DelayedFile(string name, bool fail = false) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 4;
        public string ContentType => "application/pdf";
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new DelayedStream(this, fail);

        private sealed class DelayedStream(DelayedFile file, bool fail) : MemoryStream([1, 2, 3, 4])
        {
            public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                file.Reading.TrySetResult();
                await file.Release.Task.WaitAsync(cancellationToken);
                if (fail)
                    throw new IOException("Test read failure.");
                await base.CopyToAsync(destination, bufferSize, cancellationToken);
            }
        }
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}

#pragma warning restore BL0006
