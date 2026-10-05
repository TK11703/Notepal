# Notepal
This is a combination of .Net projects that will help you keep track of notes (hand-written or digital).

Capture pages with your camera, upload photos, PDFs or Word documents, and Notepal stores the original and uses an
**Azure AI Foundry agent** to transcribe the text. You can correct the transcription, view the original and the notes
side by side, and search everything you have captured. Every user only ever sees their own notes.

## Features

- **Capture**: live camera capture in the browser (`getUserMedia`), the device camera app on phones, or upload
  JPEG/PNG/WebP/GIF images, PDF and Word (`.docx`) files. Several files/photos become the pages of a single note,
  and more pages can be appended to an existing note later (**Add pages**).
- **Original storage**: the uploaded bytes are stored unchanged in PostgreSQL (`bytea`), alongside the extracted text.
- **AI OCR**: images (and scanned PDF pages) are sent to a Foundry agent (`gpt-4.1-mini` by default) that transcribes
  handwritten or printed text. Digital PDFs and Word files are parsed locally (PdfPig / Open XML SDK). Work runs in a
  background queue and resumes automatically after a restart.
- **Review & correct**: each page has a view switcher for **Original**, **Notes** and **Side by side** (the original stays pinned while you scroll long notes; your choice is remembered per browser). Corrections are stored
  separately from the AI text, so you can revert or re-run extraction at any time.
- **Tags**: tag each note (on upload or later), with one-click suggestions from tags you have used before. Tags are
  normalized (lower case, no leading `#`), stored as a PostgreSQL `text[]` with a GIN index, shown on cards, notes and
  search results, and used to filter **My notes** (tag drop-down) and **Search** (multi-select: notes must have every selected tag,
  with or without search terms).
- **Search**: PostgreSQL full-text search (stemmed, ranked, `websearch_to_tsquery` syntax: `"phrases"`, `or`, `-exclude`)
  over corrected text and titles, plus substring matching, with highlighted snippets.
- **Sharing**: the **Share** button on a note opens a dialog where the owner searches people in the Azure tenant
  (Microsoft Graph, delegated `User.ReadBasic.All`) or types any email address, then gives each person **Reader**
  (view and download originals) or **Contributor** (also rename, tag, add, remove and reorder pages, correct text, re-run extraction) access.
  Only the owner can change sharing or delete the note. **Shared notes** in the left
  navigation lists notes *shared with me* and notes *shared by me*; on *Shared with me* you can tick one or more notes
  (or **Select all**) and **Leave** them to remove your access.
- **Per-user isolation**: Entra ID sign-in. The API only accepts access tokens for its `access_as_user` scope and
  scopes every SQL statement to the caller's object id (`oid`) or to a share addressed to them.
  A note is visible to anyone else only when its owner shares it with them (matched by `oid` or sign-in email).
- **About & FAQ pages** (`/about`, `/faq`) describing the features and answering common questions; available without signing in.
- **Modern, responsive UI** with a **light / dark / auto theme** switcher (Bootstrap 5.3 color modes plus Notepal design tokens in `wwwroot/app.css`, remembered per browser).

## Architecture

```
Browser ──(cookie, SignalR)──► Notepal.Web  (Blazor Web App, interactive server)
                                   │  OIDC sign-in, token cache, calls API on behalf of the user
                                   ▼  (internal ingress only, bearer token)
                               Notepal.Api  (ASP.NET Core 10 minimal API)
                                   │                     │
                         Npgsql (plain SQL)        Azure AI Foundry Agent Service
                                   ▼                     (managed identity)
                          PostgreSQL Flexible Server
```

| Project | Purpose |
| --- | --- |
| `src/Notepal.Web` | Blazor Web App: sign-in, upload/camera, viewer/editor, search, theme. Proxies original files from the API. |
| `src/Notepal.Api` | Minimal API with built-in validation, plain SQL data access (Npgsql) + embedded SQL migrations (`Data/Migrations/*.sql`), upload validation (extension **and** file signature), background text extraction, search. |
| `src/Notepal.AppHost` | Aspire AppHost: runs PostgreSQL (container + data volume), the API and the web app together with the Aspire dashboard. |
| `src/Notepal.ServiceDefaults` | Shared Aspire defaults: OpenTelemetry, health checks, service discovery. |
| `src/Notepal.Shared` | DTOs (with validation attributes) and limits shared by both apps. |
| `tests/Notepal.Api.Tests` | Integration tests (WebApplicationFactory + Testcontainers PostgreSQL) incl. cross-user isolation. |
| `infra/` | Bicep for Azure and the Entra ID setup script. |

### API

All endpoints (except `/healthz`) require a token with the `access_as_user` scope. Invalid input returns `400` with a
validation problem details body.

| Method & route | Description |
| --- | --- |
| `GET /api/notes?page=&pageSize=&tag=` | The caller's notes, newest first; repeat `tag` to require several tags |
| `POST /api/notes` | `multipart/form-data` with `title`, optional `tags` (repeatable, max 20) and one or more `files` (max 20 files, 20 MB each) |
| `GET /api/notes/{id}` | Note with its pages and text |
| `POST /api/notes/{id}/pages` | `multipart/form-data` with one or more `files`, appended as new pages (the note's total stays within 20 pages) |
| `PUT /api/notes/{id}` | Rename |
| `PUT /api/notes/{id}/tags` | Replace the note's tags: `{ "tags": ["biology", "exam prep"] }` |
| `GET /api/tags` | Tags the caller has used, with note counts (most used first) |
| `DELETE /api/notes/{id}` | Delete note, pages and originals |
| `GET /api/notes/{id}/pages/{pageId}/original` | Original file |
| `PUT /api/notes/{id}/pages/{pageId}/text` | Save corrected text (sending the AI text back clears the correction) |
| `POST /api/notes/{id}/pages/{pageId}/reprocess` | Re-run text extraction |
| `PUT /api/notes/{id}/pages/{pageId}/position` | Move a page: `{ "pageNumber": 1 }`; the other pages are renumbered |
| `DELETE /api/notes/{id}/pages/{pageId}` | Delete a page and its original (a note keeps at least one page) |
| `GET /api/notes/{id}/shares` | People the note is shared with (owner only) |
| `POST /api/notes/{id}/shares` | Share with a person: `{ "email": "bob@contoso.com", "displayName": "Bob", "userId": null, "permission": 0 }` (`0` Reader, `1` Contributor; re-adding updates the permission) |
| `PUT /api/notes/{id}/shares/{shareId}` | Change a person's permission (owner only) |
| `DELETE /api/notes/{id}/shares/{shareId}` | Stop sharing (owner), or leave a note shared with you (recipient) |
| `GET /api/shared/with-me?page=&pageSize=` | Notes other people shared with the caller, with role and sharer |
| `POST /api/shared/with-me/leave` | Leave notes shared with the caller: `{ "noteIds": ["…"] }` (up to 100); removes only the caller's own shares and returns `{ "left": n }` |
| `GET /api/shared/by-me?page=&pageSize=` | The caller's notes that are shared, with their recipients |
| `GET /api/search?q=&tag=&page=&pageSize=` | Full-text search, optionally limited to notes with the given tag(s); matches in `snippet` are wrapped in `⟦ ⟧`. With only `tag`, returns one result per tagged note |

## Run locally

Prerequisites: .NET 10 SDK, Docker, Azure CLI, PowerShell 7+, [Aspire CLI](https://aspire.dev) (optional).

1. Create the app registrations (once): `./infra/scripts/setup-entra.ps1 register` and note the printed ids.
2. Create a client secret for local development: `./infra/scripts/setup-entra.ps1 dev-secret`
3. Configure user secrets:
   ```bash
   cd src/Notepal.Api
   dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>"
   dotnet user-secrets set "AzureAd:ClientId" "<api-client-id>"
   # optional – enables OCR (needs `az login` and the "Azure AI User" role on the Foundry project)
   dotnet user-secrets set "Ocr:ProjectEndpoint" "https://<account>.services.ai.azure.com/api/projects/<project>"

   cd ../Notepal.Web
   dotnet user-secrets set "AzureAd:TenantId" "<tenant-id>"
   dotnet user-secrets set "AzureAd:ClientId" "<web-client-id>"
   dotnet user-secrets set "AzureAd:ClientSecret" "<secret from step 2>"
   dotnet user-secrets set "NotepalApi:Scopes:0" "api://<api-client-id>/access_as_user"
   ```
4. Start everything with Aspire: `aspire run` (or `dotnet run --project src/Notepal.AppHost`). The AppHost starts
   PostgreSQL, waits for it, then the API (which applies pending SQL migrations) and the web app. Open the dashboard
   link it prints for logs, traces and metrics, and the app at <https://localhost:7137>.

   Without Aspire: `docker compose up -d`, then `dotnet run --launch-profile https` in `src/Notepal.Api` and
   `src/Notepal.Web`.

Without `Ocr:ProjectEndpoint` everything works except image OCR: such pages are marked *Failed* with an explanation
and you can type the notes yourself.

Run the tests (Docker required): `dotnet test`

## Deploy to Azure

The Bicep template (`infra/main.bicep`) uses the cheapest options that fit the workload:

| Resource | SKU | Notes |
| --- | --- | --- |
| Container Apps environment | Consumption | Both apps: 0.25 vCPU / 0.5 GiB, **scale to zero** (min 0, max 1 replica). The API uses internal ingress only. |
| PostgreSQL Flexible Server | Burstable **B1ms**, 32 GB, no HA, 7-day LRS backups | Public access limited to Azure services, TLS required. |
| Container Registry | Shared, existing (`acracccommon` in `rg-common`) | Not created by the template. Images (`notepal-api`, `notepal-web`) are pulled with managed identities (`AcrPull`, no admin user). |
| Azure AI Foundry | AIServices S0 + project, `gpt-4.1-mini` **GlobalStandard** | Pay per token; the OCR agent is created automatically on first use. Key auth disabled. |
| Log Analytics | PerGB2018, 30 days, 1 GB/day cap | |

The web app authenticates to Entra ID with its managed identity (federated credential) – no client secrets are stored
in Azure. The API reaches Foundry with its own managed identity (`Azure AI User` role).

### First-time setup

1. `./infra/scripts/setup-entra.ps1 register` – note the API/Web client ids.
2. Create an Entra app/service principal for GitHub Actions and grant it **Contributor** and **Role Based Access Control
   Administrator** (the template creates role assignments) on the subscription or target resource group, and the same
   two roles on the shared registry's resource group (`rg-common`) so it can build images there and grant `AcrPull`. Add two
   federated credentials for this repository:
   - `repo:<owner>/<repo>:ref:refs/heads/main` – used by the what-if preview
   - `repo:<owner>/<repo>:environment:production` – used by the approved deployment
3. Configure the repository:
   - Settings → Environments: create **production**, enable **Required reviewers** and add the approvers (optionally
     restrict deployment branches to `main`).
   - Settings → Secrets and variables → Actions:
     - Secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `POSTGRES_ADMIN_PASSWORD`
     - Variables: `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `NOTEPAL_API_CLIENT_ID`, `NOTEPAL_WEB_CLIENT_ID`,
       optionally `AZURE_FOUNDRY_LOCATION` (a region offering the model as GlobalStandard) and
       `AZURE_REGISTRY_NAME` / `AZURE_REGISTRY_RESOURCE_GROUP` (default `acracccommon` / `rg-common`).
4. Push to `main`. When **CI** passes, **Deploy to Azure** starts: the *preview* job posts an infrastructure what-if in
   the run summary, then the *deploy* job waits for an approver. Once approved it deploys the infrastructure, builds both
   images in ACR and deploys the container apps. You can also start it manually from the Actions tab (approval is still
   required).
5. Once, after the first deployment, run the command printed in the workflow summary:
   `./infra/scripts/setup-entra.ps1 finalize <web-url> <web-identity-principal-id>` – this registers the redirect URI
   and trusts the web app's managed identity.

### Operational notes

- Scale to zero means the first request after idle time has a cold start of a few seconds. After a restart the
  in-memory token cache and data-protection keys are gone, so users are sent through Entra sign-in again
  (normally silent with SSO).
- Background OCR runs inside the API container; unfinished pages are re-queued automatically when it starts again.
- Legacy binary Word files (`.doc`) are not supported – save them as `.docx` or PDF. Scanned PDFs are OCR'd from the
  largest image embedded on each page.
- People search in the **Share** dialog uses Microsoft Graph with the delegated `User.ReadBasic.All` permission.
  `setup-entra.ps1 register` adds and grants it; for an existing registration, re-run `register` (or grant admin
  consent for `User.ReadBasic.All` on the web app in the Entra portal). Without consent the dialog still lets you share
  by typing an email address.
- Shares are matched to recipients by Entra object id when picked from the directory, otherwise by the sign-in email
  (`preferred_username`), so a person shared by email sees the note as soon as they sign in.
