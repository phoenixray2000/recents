using System;
using System.IO;
using Microsoft.Data.Sqlite;
class Program {
    static void Main() {
        var dbPath = Environment.GetEnvironmentVariable("LOCALAPPDATA") + @"\Recents\index.db";
        using var conn = new SqliteConnection("Data Source=" + dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT display_name, normalized_path FROM recent_items WHERE display_name LIKE '%¸±±¾%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            Console.WriteLine(reader.GetString(0) + " -> " + reader.GetString(1));
        }
    }
}
