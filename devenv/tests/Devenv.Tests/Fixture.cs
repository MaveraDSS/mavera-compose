using System.Text.Json;
using Devenv;

namespace Devenv.Tests;

public static class Fixture
{
    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static string Read(string name) => File.ReadAllText(Path(name));

    /// <summary>The real committed manifest, so the tests catch manifest mistakes too.</summary>
    public static Manifest Manifest() => Json.Load<Manifest>(Path("manifest.json"));

    public static EnvironmentSpec Dev02() => Json.Load<EnvironmentSpec>(Path("dev02.json"));

    /// <summary>The committed libertine template (placeholders only, no secrets), copied from mavera-libertine.</summary>
    public static string LibertineTemplate() => Read("libertine.appsettings.json");

    public static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}
