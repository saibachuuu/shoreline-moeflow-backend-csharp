using Microsoft.AspNetCore.Mvc.Testing;

namespace Shoreline.Tests;

/// <summary>
/// Test host that declares the English catalog on every client it hands out.
///
/// The legacy app defaults to Chinese when a request carries no
/// <c>Accept-Language</c> (Flask-Babel's <c>BABEL_DEFAULT_LOCALE = "zh"</c>,
/// app/config.py:39). Real browsers always send the header, and the assertions
/// in this suite quote the English strings, so the fixture pins the language
/// explicitly instead of relying on an implicit default. Tests that care about
/// negotiation build their own host or call <c>LanguageCatalog.ChinesePreferred</c>
/// directly.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }
}
