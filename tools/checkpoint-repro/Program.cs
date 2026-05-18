using System.Data;
using Azure.Core;
using Azure.Identity;
using Dapper;
using Microsoft.Data.SqlClient;

var server = GetArg(args, "--server");
var database = GetArg(args, "--database");

if (server is null || database is null)
{
    Console.Error.WriteLine("Usage: dotnet run -- --server <fqdn> --database <name>");
    Console.Error.WriteLine("  e.g. dotnet run -- --server ta-proddb.database.windows.net --database Admin");
    return 2;
}

var connStr = new SqlConnectionStringBuilder
{
    DataSource = server,
    InitialCatalog = database,
    Encrypt = true,
    TrustServerCertificate = false,
    ConnectTimeout = 30,
}.ConnectionString;

Console.WriteLine($"Server   : {server}");
Console.WriteLine($"Database : {database}");
Console.WriteLine($"Dapper   : {typeof(SqlMapper).Assembly.GetName().Version}");
Console.WriteLine($"SqlClient: {typeof(SqlConnection).Assembly.GetName().Version}");
Console.WriteLine();

Console.Write("Acquiring AAD token via DefaultAzureCredential... ");
var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(
    new TokenRequestContext(new[] { "https://database.windows.net/.default" }));
Console.WriteLine("done.");

await using var conn = new SqlConnection(connStr) { AccessToken = token.Token };
await conn.OpenAsync();
var whoami = await conn.QuerySingleAsync<string>("SELECT SUSER_SNAME()");
Console.WriteLine($"Connected as: {whoami}\n");

await RunCase("A. Dapper Execute(\"CHECKPOINT\")                ", () =>
    conn.ExecuteAsync("CHECKPOINT"));

await RunCase("B. Dapper Execute(\"CHECKPOINT;\")               ", () =>
    conn.ExecuteAsync("CHECKPOINT;"));

await RunCase("C. Dapper Execute(\"CHECKPOINT\", Text)          ", () =>
    conn.ExecuteAsync("CHECKPOINT", commandType: CommandType.Text));

await RunCase("D. Dapper Execute(\"CHECKPOINT;\", Text)         ", () =>
    conn.ExecuteAsync("CHECKPOINT;", commandType: CommandType.Text));

await RunCase("E. Raw SqlCommand \"CHECKPOINT\" (Text)          ", async () =>
{
    await using var cmd = new SqlCommand("CHECKPOINT", conn) { CommandType = CommandType.Text };
    await cmd.ExecuteNonQueryAsync();
});

await RunCase("F. Raw SqlCommand \"CHECKPOINT;\" (Text)         ", async () =>
{
    await using var cmd = new SqlCommand("CHECKPOINT;", conn) { CommandType = CommandType.Text };
    await cmd.ExecuteNonQueryAsync();
});

return 0;

static string? GetArg(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task RunCase(string label, Func<Task> action)
{
    try
    {
        await action();
        Console.WriteLine($"{label}  PASS");
    }
    catch (Exception ex)
    {
        var first = ex.Message.Split('\n')[0].Trim();
        Console.WriteLine($"{label}  FAIL  [{ex.GetType().Name}] {first}");
    }
}
