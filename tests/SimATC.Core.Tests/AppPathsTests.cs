namespace SimATC.Core.Tests;

/// <summary>ADR-154: la app pasó de SimATC a LUAW; la carpeta de datos vieja se renombra sola.</summary>
public class AppPathsTests
{
    [Fact]
    public void The_old_simatc_folder_is_renamed_to_luaw_keeping_its_files()
    {
        var local = Directory.CreateTempSubdirectory("luaw-paths-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(local, "SimATC", "logs"));
            File.WriteAllText(Path.Combine(local, "SimATC", "settings.json"), "{}");
            var root = AppPaths.LocalRoot(local);
            Assert.Equal(Path.Combine(local, "LUAW"), root);
            Assert.True(File.Exists(Path.Combine(root, "settings.json")));
            Assert.True(Directory.Exists(Path.Combine(root, "logs")));
            Assert.False(Directory.Exists(Path.Combine(local, "SimATC")));
        }
        finally
        {
            Directory.Delete(local, true);
        }
    }

    [Fact]
    public void An_existing_luaw_folder_is_kept_and_the_old_one_is_not_touched()
    {
        var local = Directory.CreateTempSubdirectory("luaw-paths-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(local, "LUAW"));
            Directory.CreateDirectory(Path.Combine(local, "SimATC"));
            Assert.Equal(Path.Combine(local, "LUAW"), AppPaths.LocalRoot(local));
            Assert.True(Directory.Exists(Path.Combine(local, "SimATC")));
        }
        finally
        {
            Directory.Delete(local, true);
        }
    }
}
