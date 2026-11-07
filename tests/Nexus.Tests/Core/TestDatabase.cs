using erp_inteligente.Core.Data;
using erp_inteligente.Core.Security;

namespace Nexus.Tests;

/// <summary>A fresh encrypted Nexus database in a unique temp directory, removed on dispose.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _directory;

    public TestDatabase()
    {
        _directory = Directory.CreateTempSubdirectory("nexus-tests-").FullName;
        Path = System.IO.Path.Combine(_directory, "nexus.db");
        Store = new InMemorySecretStore();
        Database = DatabaseBootstrapper.OpenOrCreate(Path, new KeyManager(Store));
    }

    public NexusDatabase Database { get; }
    public string Path { get; }
    public InMemorySecretStore Store { get; }

    public void Dispose()
    {
        Database.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
