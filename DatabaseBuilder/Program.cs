using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using CsvHelper;
using SQLitePCL;
using SQLitePCL.Ugly;
using static SQLitePCL.raw;

namespace DatabaseBuilder
{
    class Program
    {
        private static readonly Regex CategoryRegex = new Regex(@"(\d+)\s+=\s([a-zA-Z ]+)", RegexOptions.Compiled);

        static async Task Main(string csvName = "tnt/dump_release_tntvillage_2019-08-30.csv", string tntReadme = "tnt/README.txt", string dbName = "tnt.sqlite")
        {
            Batteries_V2.Init();

            if (!File.Exists(csvName))
            {
                Console.WriteLine("Invalid path for CSV file");
                return;
            }

            if (!File.Exists(tntReadme))
            {
                Console.WriteLine("Invalid path for TNT README");
                return;
            }

            var res = sqlite3_open_v2(dbName, out var db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, null);

            if (res != SQLITE_OK)
            {
                var err = db.errmsg();
                Console.WriteLine($"Error opening DB: ({res}) {err}");
                return;
            }

            CreateTables(db);

            var readme = await File.ReadAllTextAsync(tntReadme);
            var categoryMatches = CategoryRegex.Matches(readme);
            var categories = categoryMatches
                .Select(m => (id: int.Parse(m.Groups[1].ToString()), name: m.Groups[2].ToString()));

            var csvItems = ParseCsv(csvName).ToList();

            using (var stmt = db.prepare("INSERT INTO categories (id, name) VALUES (?, ?)"))
            {
                foreach (var (id, name) in categories)
                {
                    stmt.reset();

                    stmt.bind_int(1, id);
                    stmt.bind_text(2, name);

                    stmt.step_done();
                }
            }

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var dbItemCount = db.query_scalar<long>("SELECT count(*) FROM items");
            if (dbItemCount != csvItems.Count)
            {
                Console.WriteLine("Seeding DB");
                SeedDb(db, csvItems);
                Console.WriteLine("Seeding completed");
            }
            else
            {
                Console.WriteLine("Seeding not needed");
            }

            stopWatch.Stop();
            Console.WriteLine($"Stopwatch: {stopWatch.ElapsedMilliseconds} ms");
            db.exec("INSERT OR REPLACE INTO metadata (key, value) VALUES ('db_build_time_ms', ?)", stopWatch.ElapsedMilliseconds);
            db.exec("INSERT OR REPLACE INTO metadata (key, value) VALUES ('clean', 1)");
            db.close_v2();
        }

        private static void SeedDb(sqlite3 db, List<CsvItem> csvItems)
        {
            db.exec("DELETE FROM items");
            db.exec("BEGIN TRANSACTION");
            using (var stmt = db.prepare("INSERT INTO items (release_date, hash, topic, post, author, title, description, size, category, magnet) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"))
            {
                for (var i = 0; i < csvItems.Count; i++)
                {
                    if (i % 100 == 0)
                    {
                        Console.Write('.');
                    }

                    if (i % 5000 == 0)
                    {
                        Console.WriteLine();
                    }

                    var item = csvItems[i];
                    stmt.reset();

                    stmt.bind_text(1, item.ReleaseDate);
                    stmt.bind_text(2, item.Hash);
                    stmt.bind_int(3, item.Topic);
                    stmt.bind_int(4, item.Post);
                    stmt.bind_text(5, item.Author);
                    stmt.bind_text(6, item.Title);
                    stmt.bind_text(7, item.Description);
                    stmt.bind_int64(8, item.Size);
                    stmt.bind_int(9, item.Category);
                    stmt.bind_text(10, BuildMagnetUri(item));

                    stmt.step_done();
                }
            }
            db.exec("COMMIT TRANSACTION");
            Console.WriteLine();
        }

        private static string BuildMagnetUri(CsvItem item)
        {
            return new StringBuilder("magnet:?xt=urn:btih:")
                .Append(item.Hash)
                .Append("&dn=")
                .Append(HttpUtility.UrlEncode($"{item.Title}-{item.Author}"))
                .Append("&tr=http://tracker.tntvillage.scambioetico.org:2710/announce")
                .Append("&tr=udp://tracker.tntvillage.scambioetico.org:2710/announce")
                .Append("&tr=udp://tracker.coppersurfer.tk:6969/announce")
                .Append("&tr=udp://tracker.leechers-paradise.org:6969/announce")
                .Append("&tr=udp://IPv6.leechers-paradise.org:6969/announce")
                .Append("&tr=udp://tracker.internetwarriors.net:1337/announce")
                .Append("&tr=udp://tracker.tiny-vps.com:6969/announce")
                .Append("&tr=udp://tracker.mg64.net:2710/announce")
                .Append("&tr=udp://tracker.openbittorrent.com:80/announce")
                .ToString();
        }

        private static void CreateTables(sqlite3 db)
        {
            db.exec("DROP TABLE IF EXISTS categories");
            db.exec("CREATE TABLE IF NOT EXISTS categories (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
            db.exec("CREATE TABLE IF NOT EXISTS items (" + 
                "release_date TEXT NOT NULL," + 
                "hash TEXT NOT NULL," + 
                "topic INTEGER NOT NULL," + 
                "post INTEGER NOT NULL," + 
                "author TEXT NOT NULL," + 
                "title TEXT NOT NULL," + 
                "description TEXT NOT NULL," + 
                "size INTEGER NOT NULL," + 
                "category INTEGER NOT NULL," + 
                "magnet TEXT DEFAULT NULL," +
                "disabled INTEGER DEFAULT 0" +
                ")");
            db.exec("CREATE TABLE IF NOT EXISTS metadata (key TEXT NOT NULL, value TEXT)");
            db.exec("INSERT OR REPLACE INTO metadata (key, value) VALUES ('version', '1')");
        }

        private static IEnumerable<CsvItem> ParseCsv(string csvName)
        {
            using (var reader = File.OpenText(csvName))
            using (var csv = new CsvReader(reader))
            {
                csv.Configuration.Delimiter = ",";
                csv.Configuration.MissingFieldFound = null;

                // skip the header
                csv.Read();

                while (csv.Read())
                {
                    var releaseDate = csv.GetField<string>(0);
                    var hash = csv.GetField<string>(1);
                    var topic = csv.GetField<int>(2);
                    var post = csv.GetField<int>(3);
                    var author = csv.GetField<string>(4);
                    var title = csv.GetField<string>(5);
                    var description = csv.GetField<string>(6);
                    var size = csv.GetField<long>(7);
                    var category = csv.GetField<int>(8);

                    var item = new CsvItem(releaseDate, hash, topic, post, author, title, description, size, category);

                    yield return item;
                }
            }
        }
    }

    struct CsvItem
    {
        public string ReleaseDate { get; }
        public string Hash { get; }
        public int Topic { get; }
        public int Post { get; }
        public string Author { get; }
        public string Title { get; }
        public string Description { get; }
        public long Size { get; }
        public int Category { get; }

        public CsvItem(string releaseDate, string hash, int topic, int post, string author, string title, string description, long size, int category)
        {
            ReleaseDate = releaseDate;
            Hash = hash;
            Topic = topic;
            Post = post;
            Author = author;
            Title = title;
            Description = description;
            Size = size;
            Category = category;
        }
    }
}
