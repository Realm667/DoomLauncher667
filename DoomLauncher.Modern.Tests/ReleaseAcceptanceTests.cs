using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DoomLauncher.WinUI.Services;
using Microsoft.Data.Sqlite;

internal static class ReleaseAcceptanceTests
{
    private sealed class Locator(string path):IDoomLauncherDatabaseLocator { public string FindDatabase()=>path; }
    private sealed record TableSnapshot(string[] Columns,string Rows);
    private sealed record Snapshot(Dictionary<string,TableSnapshot> Tables,Dictionary<string,string> Files);
    private static readonly string[] UserTables=["GameFiles","Files","Tags","TagMapping","SourcePorts","IWads","Stats","Configuration","WinUI_GameState","WinUI_IdGamesMetadata","WinUI_IwadMetadata","WinUI_SourcePortCapabilities"];
    private static SqliteConnection Open(string path)=>new(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());
    private static async Task Exec(SqliteConnection db,string sql) { await using var c=db.CreateCommand();c.CommandText=sql;await c.ExecuteNonQueryAsync(); }
    private static void Check(bool value,string name) {if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS "+name);}
    private static string Quote(string value)=>"["+value.Replace("]","]]")+"]";

    public static async Task PrepareHistoricalAsync(string package,string instance)
    {
        Check(!File.Exists(Path.Combine(instance,"DoomLauncher.sqlite")),"Historical test destination has no user database");
        var winui=Path.Combine(package,"WinUI");
        var database=Path.Combine(instance,"DoomLauncher.sqlite");
        File.Copy(Path.Combine(winui,"Assets","DoomLauncher.Empty.sqlite"),database);
        // Apply the schema from the actual old distributed assembly, not today's
        // schema pretending to be a historical installation.
        using(var context=new HistoricalContext(Path.Combine(winui,"DoomLauncher.WinUI.dll")))
        {
            var assembly=context.LoadFromAssemblyPath(Path.Combine(winui,"DoomLauncher.WinUI.dll"));
            var schema=assembly.GetType("DoomLauncher.WinUI.Services.WinUiDatabaseSchema",true)!;
            var method=schema.GetMethod("EnsureAsync",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;
            var connectionType=method.GetParameters()[0].ParameterType;
            var connection=(IDisposable)Activator.CreateInstance(connectionType,"Data Source="+database+";Pooling=False")!;
            try
            {
                connectionType.GetMethod("Open",Type.EmptyTypes)!.Invoke(connection,null);
                var arguments=method.GetParameters().Select((p,i)=>i==0?(object)connection:i==1?CancellationToken.None:p.DefaultValue).ToArray();
                await (Task)method.Invoke(null,arguments)!;
            }
            finally {connection.Dispose();}
        }
        var managedReference="Data\\";
        await using(var db=Open(database))
        {
            await db.OpenAsync();
            await using(var configured=db.CreateCommand()){configured.CommandText="SELECT Value FROM Configuration WHERE Name='GameFileDirectory';";managedReference=Convert.ToString(await configured.ExecuteScalarAsync())??managedReference;}
            await Exec(db,"INSERT INTO GameFiles(GameFileID,FileName,Title,Author,Map,MapCount,MinutesPlayed,IsSyncNeeded) VALUES(90001,'Mods/upgrade.zip','Upgrade sentinel','Release test','MAP01',1,42,1),(90002,'GameWads/DOOM2.WAD','Doom II sentinel','id Software','MAP01',1,7,1); INSERT INTO Tags(TagID,Name,HasTab) VALUES(90001,'Upgrade collection',0); INSERT INTO TagMapping(FileID,TagID) VALUES(90001,90001); INSERT INTO WinUI_GameState(GameFileID,Finished) VALUES(90001,1);");
            await using(var port=db.CreateCommand())
            {
                port.CommandText="INSERT INTO SourcePorts(SourcePortID,Name,Directory,Executable,SupportedExtensions,FileOption,ExtraParameters,Archived,LaunchType) VALUES(90001,'User sourceport',$directory,'user.exe','.wad','-file','-nosound',0,0); INSERT INTO IWads(IWadID,Name,FileName,GameFileID) VALUES(90001,'Doom II sentinel','DOOM2.WAD',90002); UPDATE GameFiles SET SourcePortID=90001,IwadID=90001 WHERE GameFileID=90001;";
                port.Parameters.AddWithValue("$directory",Path.Combine(managedReference,"Sourceports","UserPort"));await port.ExecuteNonQueryAsync();
            }
            await Exec(db,"INSERT OR REPLACE INTO WinUI_Migrations(MigrationKey,CompletedAt) VALUES('FirstSetupWizardV2','2026-01-01');");
            foreach(var (table,sql) in new[] {
                ("WinUI_PersonalMods","INSERT INTO WinUI_PersonalMods(GameFileID,PersonalRating,QueuePosition,AutoDequeue,UpdatedUtc) VALUES(90001,5,0,1,'2026-01-01');"),
                ("WinUI_MapProgress","INSERT INTO WinUI_MapProgress(GameFileID,MapName,Finished,UpdatedUtc) VALUES(90001,'MAP01',1,'2026-01-01');"),
                ("WinUI_MetadataLocks","INSERT INTO WinUI_MetadataLocks(GameFileID,Field) VALUES(90001,'Title');"),
                ("WinUI_MetadataHistory","INSERT INTO WinUI_MetadataHistory(GameFileID,AtUtc,Source,BeforeJson,AfterJson) VALUES(90001,'2026-01-01','User','{}','{}');") })
            {
                await using var exists=db.CreateCommand();exists.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";exists.Parameters.AddWithValue("$name",table);
                if(Convert.ToInt32(await exists.ExecuteScalarAsync())>0)await Exec(db,sql);
            }
        }
        Check(!Path.IsPathFullyQualified(managedReference),"Historical fixture has a portable managed root");
        WriteArchive(Path.Combine(instance,managedReference,"Mods","upgrade.zip"));
        var protectedFiles=new Dictionary<string,byte[]>{
            [Path.Combine(managedReference,"GameWads","DOOM2.WAD")]=Wad(),
            [Path.Combine(managedReference,"Sourceports","UserPort","user.exe")]=Encoding.UTF8.GetBytes("Synthetic; never executed."),
            [Path.Combine(managedReference,"SaveGames","personal.zds")]=Encoding.UTF8.GetBytes("Personal save sentinel"),
            ["Data/UserState/personal-notes.txt"]=Encoding.UTF8.GetBytes("User-owned preferences sentinel"),
            ["Data/TileImages/colored/doom.png"]=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j0P8AAAAASUVORK5CYII="),
        };
        foreach(var (relative,bytes) in protectedFiles){var target=Path.Combine(instance,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);await File.WriteAllBytesAsync(target,bytes);}
        Directory.CreateDirectory(Path.Combine(instance,"Data","Themes"));
        var theme=Path.Combine(package,"WinUI","Assets","Themes","Dark.xml");
        if(File.Exists(theme))File.Copy(theme,Path.Combine(instance,"Data","Themes","Dark.xml"),true);
        await File.WriteAllTextAsync(Path.Combine(instance,".release-test-instance"),"isolated historical update fixture");
        await using(var db=Open(database)){await db.OpenAsync();await SaveSnapshotAsync(db,instance,Path.Combine(instance,"before-upgrade.json"));}
        // Early betas started through CMD and have no bootstrap EXE yet. A full
        // folder backup is the documented rollback contract across that boundary.
        ZipFile.CreateFromDirectory(instance,instance+"-before.zip");
        Console.WriteLine("HISTORICAL_FIXTURE "+instance);
    }

    public static async Task VerifyHistoricalAsync(string instance)
    {
        var snapshot=JsonSerializer.Deserialize<Snapshot>(await File.ReadAllTextAsync(Path.Combine(instance,"before-upgrade.json")))!;
        await using(var db=Open(Path.Combine(instance,"DoomLauncher.sqlite")))
        {
            await db.OpenAsync();await WinUiDatabaseSchema.EnsureAsync(db,default);
            foreach(var (name,before) in snapshot.Tables)
            {
                var after=await Rows(db,name,before.Columns);
                using var newRows=JsonDocument.Parse(after);using var oldRows=JsonDocument.Parse(before.Rows);
                var current=newRows.RootElement.EnumerateArray().Select(r=>r.GetRawText()).ToHashSet();
                foreach(var row in oldRows.RootElement.EnumerateArray())
                {
                    var expected=ExpectedUpgradeRow(name,before.Columns,row);
                    Check(current.Contains(expected),"Historical upgrade preserves original "+name+" row "+row[0]+" (documented managed-path flattening allowed)");
                }
                Check(newRows.RootElement.GetArrayLength()>=oldRows.RootElement.GetArrayLength(),"Historical upgrade retains every original "+name+" row");
            }
            await using var check=db.CreateCommand();check.CommandText="PRAGMA integrity_check;";
            Check(Convert.ToString(await check.ExecuteScalarAsync())=="ok","Historical upgraded database integrity");
        }
        foreach(var (relative,hash) in snapshot.Files)
        {
            var normalized=NormalizeManagedPath(relative);var destination=Path.Combine(instance,normalized);
            if(normalized.StartsWith("Data\\TileImages\\",StringComparison.OrdinalIgnoreCase) && TileImageDefaults.IsLegacyDefaultHash(hash))
            {
                var leaf=Path.GetRelativePath("Data\\TileImages",normalized);
                if(!leaf.Contains('\\'))Check(!File.Exists(destination),"Known obsolete root placeholder is removed: "+relative);
                else Check(File.Exists(destination) && Hash(destination)==Hash(Path.Combine(instance,"WinUI","Assets","Library",leaf)),"Known obsolete placeholder upgrades to the shipped high-resolution default: "+relative);
            }
            else Check(Hash(destination)==hash,"Historical update preserves personal file "+relative);
        }
        var restored=instance+"-rollback";
        ZipFile.ExtractToDirectory(instance+"-before.zip",restored);
        foreach(var (relative,hash) in snapshot.Files)Check(Hash(Path.Combine(restored,relative))==hash,"Rollback preserves "+relative);
        await using(var db=Open(Path.Combine(restored,"DoomLauncher.sqlite")))
        {await db.OpenAsync();foreach(var (name,before) in snapshot.Tables)Check(await Rows(db,name,before.Columns)==before.Rows,"Rollback restores original "+name);}
    }

    public static async Task PrepareLargeMigrationAsync(string root,int count)
    {
        if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DoomLauncherDatabaseLocator.DatabaseEnvironmentVariable)))
            throw new InvalidOperationException("Unset the database environment override before running an isolated migration fixture.");
        var legacy=Path.Combine(root,"original");var target=Path.Combine(root,"portable");
        Check(!Directory.Exists(root),"Large migration fixture starts empty");Directory.CreateDirectory(legacy);
        var dbPath=Path.Combine(legacy,"DoomLauncher.sqlite");File.Copy(Path.Combine(AppContext.BaseDirectory,"Assets","DoomLauncher.Empty.sqlite"),dbPath);
        await using(var db=Open(dbPath))
        {
            await db.OpenAsync();using var transaction=db.BeginTransaction();
            await using var insert=db.CreateCommand();insert.Transaction=transaction;
            insert.CommandText="INSERT INTO GameFiles(GameFileID,FileName,Title,Author,Map,MapCount,MinutesPlayed,IsSyncNeeded) VALUES($id,$file,$title,'Migration fixture','MAP01',1,0,1); INSERT INTO TagMapping(FileID,TagID) VALUES($id,$tag);";
            var id=insert.Parameters.Add("$id",SqliteType.Integer);var file=insert.Parameters.Add("$file",SqliteType.Text);var title=insert.Parameters.Add("$title",SqliteType.Text);var tag=insert.Parameters.Add("$tag",SqliteType.Integer);
            await using(var tags=db.CreateCommand()){tags.Transaction=transaction;tags.CommandText="INSERT INTO Tags(TagID,Name,HasTab) VALUES "+string.Join(',',Enumerable.Range(1,12).Select(i=>$"({i},'Collection {i:00}',0)"))+"; UPDATE Configuration SET Value='GameFiles' WHERE Name='GameFileDirectory';";await tags.ExecuteNonQueryAsync();}
            for(var index=1;index<=count;index++)
            {
                var name=$"migration-{index:00000}.zip";id.Value=index;file.Value=name;title.Value=$"Migration mod {index:00000}";tag.Value=(index-1)%12+1;
                await insert.ExecuteNonQueryAsync();WriteArchive(Path.Combine(legacy,"GameFiles",name));
            }
            transaction.Commit();
        }
        var sourceHash=Hash(dbPath);
        var migration=new LegacyInstallationMigrationService(new Locator(Path.Combine(target,"DoomLauncher.sqlite")));
        var result=await migration.MigrateAsync(legacy);
        Check(Path.GetFullPath(result.DatabasePath)==Path.Combine(target,"DoomLauncher.sqlite"),"Migration honors the explicitly supplied portable destination");
        Check(result.CopiedFiles>=count && Hash(dbPath)==sourceHash,"Large migration copies archives without changing the source database");
        var locator=new Locator(result.DatabasePath);var service=new SqliteNativeLibraryService(locator);
        await new FirstSetupService(locator,service).CompleteWizardAsync();
        var catalog=await new SqliteLibraryCatalog(locator,new UiLocalization()).LoadAsync();
        Check(catalog.Entries.Count==count,"Large migration retains every entry");
        await File.WriteAllTextAsync(Path.Combine(target,".release-test-instance"),"isolated large legacy migration");
        await File.WriteAllTextAsync(Path.Combine(target,"expected-entry-count.txt"),count.ToString());
        Console.WriteLine("LARGE_MIGRATION_FIXTURE "+target);
    }

    private static async Task SaveSnapshotAsync(SqliteConnection db,string root,string destination)
    {
        var tables=new Dictionary<string,TableSnapshot>();
        var allTables=new List<string>(UserTables);
        await using(var tableQuery=db.CreateCommand())
        {
            tableQuery.CommandText="SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'WinUI_%' AND name NOT IN ('WinUI_Migrations','WinUI_SchemaRevision');";
            await using var reader=await tableQuery.ExecuteReaderAsync();while(await reader.ReadAsync())allTables.Add(reader.GetString(0));
        }
        foreach(var name in allTables.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var columns=db.CreateCommand();columns.CommandText="PRAGMA table_info("+Quote(name)+");";var names=new List<string>();
            await using(var reader=await columns.ExecuteReaderAsync())while(await reader.ReadAsync())names.Add(reader.GetString(1));
            if(names.Count>0)tables[name]=new(names.ToArray(),await Rows(db,name,names.ToArray()));
        }
        var files=Directory.EnumerateFiles(Path.Combine(root,"Data"),"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(root,p),Hash);
        await File.WriteAllTextAsync(destination,JsonSerializer.Serialize(new Snapshot(tables,files)));
    }
    private static async Task<string> Rows(SqliteConnection db,string table,string[] columns)
    {
        await using var command=db.CreateCommand();command.CommandText="SELECT "+string.Join(',',columns.Select(Quote))+" FROM "+Quote(table)+" ORDER BY rowid;";
        var rows=new List<object[]>();await using var reader=await command.ExecuteReaderAsync();
        while(await reader.ReadAsync()){var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(values.Select(v=>v is DBNull?null!:v).ToArray());}
        return JsonSerializer.Serialize(rows);
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string NormalizeManagedPath(string value)
    {
        var windows=value.Replace('/','\\');const string old="Data\\GameFiles\\";
        return windows.StartsWith(old,StringComparison.OrdinalIgnoreCase)?"Data\\"+windows[old.Length..]:value;
    }
    private static string ExpectedUpgradeRow(string table,string[] columns,JsonElement row)
    {
        var values=row.EnumerateArray().Select(v=>(object)v).ToArray();
        if(table=="Configuration")
        {
            var name=Array.FindIndex(columns,c=>c.Equals("Name",StringComparison.OrdinalIgnoreCase));var value=Array.FindIndex(columns,c=>c.Equals("Value",StringComparison.OrdinalIgnoreCase));
            if(new[]{"GameFileDirectory","ScreenshotDirectory","TempDirectory","GameWadDirectory","DemoDirectory","SaveGameDirectory"}.Contains(row[name].GetString()))
            {
                var path=row[value].GetString()??"";var normalized=NormalizeManagedPath(path);
                if(row[name].GetString()=="GameFileDirectory" && normalized.TrimEnd('\\','/').Equals("Data",StringComparison.OrdinalIgnoreCase))normalized="Data\\";
                values[value]=normalized;
            }
        }
        if(table=="SourcePorts")
        {var index=Array.FindIndex(columns,c=>c.Equals("Directory",StringComparison.OrdinalIgnoreCase));if(index>=0 && row[index].ValueKind==JsonValueKind.String)values[index]=NormalizeManagedPath(row[index].GetString()!);}
        return JsonSerializer.Serialize(values);
    }
    private static byte[] Wad(){using var stream=new MemoryStream();using var writer=new BinaryWriter(stream);writer.Write(Encoding.ASCII.GetBytes("PWAD"));writer.Write(1);writer.Write(12);writer.Write(12);writer.Write(0);writer.Write(Encoding.ASCII.GetBytes("MAP01\0\0\0"));return stream.ToArray();}
    private static void WriteArchive(string path){Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var zip=ZipFile.Open(path,ZipArchiveMode.Create);using var output=zip.CreateEntry("fixture.wad").Open();output.Write(Wad());}
    private sealed class HistoricalContext(string path):AssemblyLoadContext(true),IDisposable
    {
        private readonly AssemblyDependencyResolver resolver=new(path);
        protected override Assembly? Load(AssemblyName name){var file=resolver.ResolveAssemblyToPath(name);return file is null?null:LoadFromAssemblyPath(file);}
        protected override nint LoadUnmanagedDll(string name){var file=resolver.ResolveUnmanagedDllToPath(name);return file is null?0:LoadUnmanagedDllFromPath(file);}
        public void Dispose()=>Unload();
    }
}
