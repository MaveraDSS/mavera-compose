using Devenv;
using Xunit;

namespace Devenv.Tests;

/// <summary>A failing health entry whose dependency was never configured is "degraded", not a failed start.</summary>
public class HealthTests
{
    private const string S3Only = """
        {"status":"Unhealthy","totalDuration":"00:00:28","entries":{
          "sql-server":{"status":"Healthy","duration":"0","tags":["ready"]},
          "rabbitmq":{"status":"Healthy","duration":"0","tags":["ready"]},
          "s3":{"status":"Unhealthy","duration":"28","description":"Unable to get IAM security credentials","tags":["ready"]}}}
        """;

    private static readonly Dictionary<string, string> TolerateS3 = new() { ["s3"] = "S3_ACCESS_KEY_ID blank in secrets.json: documents cannot be opened" };

    [Fact]
    public void AToleratedFailureIsOkAndDegradedWithTheNoteInTheDetail()
    {
        var r = Health.Classify(503, S3Only, TolerateS3);
        Assert.True(r.Ok);
        Assert.True(r.IsDegraded);
        Assert.Equal(new[] { "s3" }, r.Degraded);
        Assert.Contains("degraded: s3 (S3_ACCESS_KEY_ID blank", r.Detail);
        Assert.StartsWith("HTTP 503", r.Detail);
    }

    [Fact]
    public void AnUntoleratedFailureStaysAFailureEvenNextToAToleratedOne()
    {
        var body = S3Only.Replace("\"sql-server\":{\"status\":\"Healthy\"", "\"sql-server\":{\"status\":\"Unhealthy\"");
        var r = Health.Classify(503, body, TolerateS3);
        Assert.False(r.Ok);
        Assert.False(r.IsDegraded);
        Assert.Contains("failing: s3, sql-server", r.Detail);
    }

    [Fact]
    public void NothingIsToleratedWithoutAnEntryForIt()
    {
        var r = Health.Classify(503, S3Only, new Dictionary<string, string>());
        Assert.False(r.Ok);
        Assert.Contains("failing: s3", r.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unhealthy")]
    [InlineData("{\"status\":\"Unhealthy\"}")]
    [InlineData("<html>bad gateway</html>")]
    public void ABodyWithoutEntriesIsAPlainFailure(string? body)
    {
        var r = Health.Classify(503, body, TolerateS3);
        Assert.False(r.Ok);
        Assert.Equal("HTTP 503", r.Detail);
        Assert.Null(Health.FailingEntries(body));
    }

    [Fact]
    public void DocumentServiceToleratesS3OnlyWhileItsSecretsAreBlank()
    {
        var service = Fixture.Manifest().FindService("document-service")!;
        Assert.True(service.OptionalHealthChecks.ContainsKey("s3"));
        var exampleKeys = SecretsSetup.ParseExample(Fixture.Read("secrets.example.json")).Select(e => e.Key).ToHashSet();
        Assert.All(service.OptionalHealthChecks["s3"].Secrets, k => Assert.Contains(k, exampleKeys));

        var blank = service.ToleratedHealthChecks(new Dictionary<string, string> { ["S3_ACCESS_KEY_ID"] = "" });
        Assert.Contains("s3", blank.Keys);
        Assert.Contains("S3_ENDPOINT_URL", blank["s3"]);
        Assert.Contains("DSS-5604", blank["s3"]);

        var filled = service.OptionalHealthChecks["s3"].Secrets.ToDictionary(k => k, _ => "value");
        Assert.Empty(service.ToleratedHealthChecks(filled));
    }

    [Fact]
    public void OtherServicesTolerateNothingByDefault()
    {
        var service = Fixture.Manifest().FindService("evaluation-service")!;
        Assert.Empty(service.ToleratedHealthChecks(new Dictionary<string, string>()));
    }
}
