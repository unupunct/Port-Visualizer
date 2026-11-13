using Microsoft.Data.Sqlite;

namespace PortVisualizer.Data;

public static class AppPaths
{
    public static string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortVisualizer");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string BootstrapFile => Path.Combine(DataDirectory, "bootstrap.json");
    public static string DefaultDatabasePath => Path.Combine(DataDirectory, "portvisualizer.db");
}

/// <summary>Owns the SQLite file, connection creation and schema migrations.</summary>
public sealed class PortVisualizerDatabase
{
    private readonly string _connectionString;

    public PortVisualizerDatabase(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private static readonly string[] Migrations =
    {
        // v1
        """
        CREATE TABLE Processes (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ProcessId INTEGER NOT NULL,
            ProcessName TEXT NOT NULL,
            ExecutablePath TEXT,
            StartTimeUtc TEXT,
            FirstSeen TEXT NOT NULL,
            LastSeen TEXT NOT NULL);
        CREATE UNIQUE INDEX IX_Processes_Identity ON Processes(ProcessId, IFNULL(StartTimeUtc, ''));
        CREATE INDEX IX_Processes_Name ON Processes(ProcessName);
        CREATE INDEX IX_Processes_LastSeen ON Processes(LastSeen);

        CREATE TABLE ConnectionEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc TEXT NOT NULL,
            EventType TEXT NOT NULL,
            ProcessId INTEGER NOT NULL,
            ProcessRef INTEGER REFERENCES Processes(Id) ON DELETE SET NULL,
            ProcessName TEXT NOT NULL,
            Protocol TEXT NOT NULL,
            LocalAddress TEXT NOT NULL,
            LocalPort INTEGER NOT NULL,
            RemoteAddress TEXT,
            RemotePort INTEGER NOT NULL,
            ConnectionState TEXT NOT NULL,
            PreviousState TEXT);
        CREATE INDEX IX_Events_Time ON ConnectionEvents(TimestampUtc);
        CREATE INDEX IX_Events_TypeTime ON ConnectionEvents(EventType, TimestampUtc);
        CREATE INDEX IX_Events_Process ON ConnectionEvents(ProcessName, TimestampUtc);
        CREATE INDEX IX_Events_Remote ON ConnectionEvents(RemoteAddress);
        CREATE INDEX IX_Events_RemotePort ON ConnectionEvents(RemotePort);
        CREATE INDEX IX_Events_LocalPort ON ConnectionEvents(LocalPort);
        CREATE INDEX IX_Events_ProcessRef ON ConnectionEvents(ProcessRef);

        CREATE TABLE CurrentConnections (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            ConnectionKey TEXT NOT NULL UNIQUE,
            ProcessId INTEGER NOT NULL,
            Protocol TEXT NOT NULL,
            LocalAddress TEXT NOT NULL,
            LocalPort INTEGER NOT NULL,
            RemoteAddress TEXT,
            RemotePort INTEGER NOT NULL,
            ConnectionState TEXT NOT NULL,
            FirstObservedUtc TEXT NOT NULL,
            LastObservedUtc TEXT NOT NULL);
        CREATE INDEX IX_Current_Process ON CurrentConnections(ProcessId);

        CREATE TABLE AlertRules (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            RuleName TEXT NOT NULL,
            RuleType TEXT NOT NULL,
            RuleValue TEXT NOT NULL DEFAULT '',
            IsEnabled INTEGER NOT NULL DEFAULT 1,
            Severity TEXT NOT NULL DEFAULT 'Warning');

        CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """,
    };

    public static int LatestVersion => Migrations.Length;

    public int GetVersion()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Applies pending migrations, each in its own transaction. Safe to call on every start.</summary>
    public void Migrate()
    {
        using var c = Open();
        int version;
        using (var cmd = c.CreateCommand()) { cmd.CommandText = "PRAGMA user_version"; version = Convert.ToInt32(cmd.ExecuteScalar()); }
        for (var v = version; v < Migrations.Length; v++)
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[v];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version = {v + 1}";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public void Backup(string destinationPath)
    {
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", destinationPath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Releases file handles so the database file can be moved or deleted.</summary>
    public static void ReleaseFiles() => SqliteConnection.ClearAllPools();
}
