using Microsoft.Data.Sqlite;

namespace YTDownloadTool.Core.Storage;

public interface ISchemaMigration
{
    int TargetVersion { get; }
    string Description { get; }
    void Apply(SqliteConnection connection, SqliteTransaction transaction);
}
