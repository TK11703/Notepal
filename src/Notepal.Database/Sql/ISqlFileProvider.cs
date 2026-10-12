namespace Notepal.Database;

public interface ISqlFileProvider
{
    string Get(string relativePath);
}
