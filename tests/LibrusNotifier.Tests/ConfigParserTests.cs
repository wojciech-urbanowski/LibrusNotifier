using System.Text;
using LibrusNotifier.Configuration;

namespace LibrusNotifier.Tests;

public sealed class ConfigParserTests
{
    [Fact]
    public void ParsesSampleConfiguration()
    {
        var config = SampleConfig.Parse(SampleConfig.Text);
        Assert.Equal("LOGIN_RODZICA", config.Parent.Login);
        Assert.Equal("HASLO_RODZICA", config.Parent.Password);
        Assert.Equal("LOGIN_UCZNIA", config.Student.Login);
        Assert.Equal("HASLO_UCZNIA", config.Student.Password);
        Assert.Equal(TimeSpan.FromMinutes(9), config.IdleLimit);
        Assert.Equal("nadawca@gmail.com", config.Gmail.Email);
        Assert.Equal("abcd efgh ijkl mnop", config.Gmail.AppPassword);
        Assert.Equal(["rodzic@example.com"], config.ParentRecipients);
        Assert.Equal(["uczen@example.com"], config.StudentRecipients);
        Assert.Equal(["administrator@example.com"], config.AdminRecipients);
        Assert.Equal("PL-DS", config.Voivodeship.Code);
        Assert.DoesNotContain("HASLO", config.Parent.ToString());
        Assert.DoesNotContain("abcd", config.Gmail.ToString());
    }

    [Fact]
    public void ExampleFileInRepositoryIsValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "librus_config.txt");
        var config = ConfigParser.Load(Path.GetFullPath(path));
        Assert.NotEmpty(config.ParentRecipients);
    }

    [Fact]
    public void SessionSectionIsOptional()
    {
        var text = SampleConfig.Text.Replace("[sesja]\nlimit_bezczynnosci_minuty=9\n", "").Replace("[sesja]\r\nlimit_bezczynnosci_minuty=9\r\n", "");
        Assert.DoesNotContain("[sesja]", text);
        Assert.Equal(ConfigParser.DefaultIdleLimit, SampleConfig.Parse(text).IdleLimit);
    }

    [Theory]
    [InlineData("7.5", 7.5)]
    [InlineData("1", 1)]
    [InlineData("0.25", 0.25)]
    public void AcceptsPositiveIdleLimit(string value, double minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SampleConfig.Parse(SampleConfig.Text.Replace("limit_bezczynnosci_minuty=9", "limit_bezczynnosci_minuty=" + value)).IdleLimit);

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9,5")]
    [InlineData("abc")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999999")]
    [InlineData("")]
    public void RejectsInvalidIdleLimit(string value)
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace("limit_bezczynnosci_minuty=9", "limit_bezczynnosci_minuty=" + value)));
        Assert.Contains("[sesja]", error.Message);
    }

    [Fact]
    public void SessionSectionRequiresKey()
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace("limit_bezczynnosci_minuty=9", "")));
        Assert.Contains("limit_bezczynnosci_minuty", error.Message);
    }

    [Fact]
    public void ValuesAreLiteralAndSplitOnFirstEquals()
    {
        var config = SampleConfig.Parse(SampleConfig.Text.Replace("haslo=HASLO_RODZICA", "haslo=a=b#c;d\\n$HOME"));
        Assert.Equal("a=b#c;d\\n$HOME", config.Parent.Password);
    }

    [Fact]
    public void AddressListsTrimAndRemoveDuplicatesCaseInsensitively()
    {
        var config = SampleConfig.Parse(SampleConfig.Text.Replace("rodzic@example.com",
            "  rodzic@example.com  \n; komentarz\nRODZIC@example.com\nmama=tata@example.com\n   # wcięty komentarz"));
        Assert.Equal(["rodzic@example.com", "mama=tata@example.com"], config.ParentRecipients);
    }

    [Fact]
    public void StudentListMayBeEmpty()
    {
        var config = SampleConfig.Parse(SampleConfig.Text.Replace("uczen@example.com", ""));
        Assert.Empty(config.StudentRecipients);
    }

    [Theory]
    [InlineData("[odbiorcy_rodzice]", "rodzic@example.com", "odbiorcy_rodzice")]
    [InlineData("[odbiorcy_log]", "administrator@example.com", "odbiorcy_log")]
    public void RequiredListsNeedAtLeastOneAddress(string _, string address, string section)
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace(address, "")));
        Assert.Contains($"[{section}]", error.Message);
    }

    [Theory]
    [InlineData("[rodzic]")]
    [InlineData("[uczen]")]
    [InlineData("[gmail]")]
    [InlineData("[kalendarz]")]
    [InlineData("[odbiorcy_uczen]")]
    public void MissingRequiredSectionIsAnError(string header)
    {
        var text = SampleConfig.Text.Replace(header, "[sesja2]");
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(text));
        Assert.Contains("librus_config.txt", error.Message);
    }

    [Fact]
    public void ReportsMissingSectionByName()
    {
        var text = string.Join('\n', SampleConfig.Text.Split('\n').Where(l => !l.StartsWith("[kalendarz]") && !l.StartsWith("wojewodztwo")));
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(text));
        Assert.Contains("brak wymaganej sekcji [kalendarz]", error.Message);
    }

    [Theory]
    [InlineData("[nieznana]", "nieznana sekcja [nieznana]")]
    [InlineData("[rodzic]\nlogin=x", "więcej niż raz")]
    public void UnknownOrRepeatedSectionsAreErrors(string addition, string expected)
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text + "\n" + addition + "\n"));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void UnknownKeyIsErrorWithLineNumber()
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace("login=LOGIN_RODZICA", "login=LOGIN_RODZICA\nuzytkownik=x")));
        Assert.Contains("nieznany klucz 'uzytkownik'", error.Message);
        Assert.Contains("wiersz 3", error.Message);
        Assert.Contains("[rodzic]", error.Message);
    }

    [Fact]
    public void RepeatedKeyIsError()
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace("login=LOGIN_UCZNIA", "login=LOGIN_UCZNIA\nlogin=INNY")));
        Assert.Contains("'login' występuje więcej niż raz", error.Message);
    }

    [Fact]
    public void EntryBeforeFirstSectionIsError()
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse("login=x\n" + SampleConfig.Text));
        Assert.Contains("wiersz 1", error.Message);
    }

    [Theory]
    [InlineData("rodzic@example.com", "nie-adres")]
    [InlineData("rodzic@example.com", "Jan <jan@example.com>")]
    [InlineData("rodzic@example.com", "jan@localhost")]
    [InlineData("rodzic@example.com", "jan kowalski@example.com")]
    [InlineData("email=nadawca@gmail.com", "email=zly-adres")]
    public void InvalidAddressesAreErrors(string original, string replacement)
    {
        var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace(original, replacement)));
        Assert.Contains("niepoprawny adres", error.Message);
    }

    [Theory]
    [InlineData("dolnośląskie", "PL-DS")]
    [InlineData("Mazowieckie", "PL-MZ")]
    [InlineData("slaskie", "PL-SK")]
    [InlineData("swietokrzyskie", "PL-SL")]
    [InlineData("lubelskie", "PL-LB")]
    [InlineData("lubuskie", "PL-LU")]
    [InlineData("podkarpackie", "PL-PD")]
    [InlineData("podlaskie", "PL-PK")]
    public void VoivodeshipMapsToOpenHolidaysCode(string name, string code) =>
        Assert.Equal(code, SampleConfig.Parse(SampleConfig.Text.Replace("dolnoslaskie", name)).Voivodeship.Code);

    [Fact]
    public void UnknownVoivodeshipIsError() =>
        Assert.Contains("nieznane województwo", Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace("dolnoslaskie", "bawaria"))).Message);

    [Theory]
    [InlineData("haslo=HASLO_RODZICA", "haslo=")]
    [InlineData("login=LOGIN_UCZNIA", "")]
    public void MissingOrEmptyCredentialsAreErrors(string original, string replacement) =>
        Assert.Throws<ConfigException>(() => SampleConfig.Parse(SampleConfig.Text.Replace(original, replacement)));

    [Fact]
    public void ErrorsNeverContainPasswords()
    {
        const string secret = "TajneHaslo#123=xyz";
        var text = SampleConfig.Text.Replace("haslo=HASLO_RODZICA", "haslo=" + secret)
            .Replace("app_password=abcd efgh ijkl mnop", "app_password=" + secret);
        foreach (var broken in new[]
                 {
                     text.Replace("login=LOGIN_RODZICA", secret),
                     text.Replace("rodzic@example.com", "haslo=" + secret),
                     text.Replace("wojewodztwo=dolnoslaskie", "wojewodztwo=" + secret),
                     text.Replace("[uczen]", "[" + secret + "]"),
                     text.Replace("login=LOGIN_UCZNIA", secret + "=x")
                 })
        {
            var error = Assert.Throws<ConfigException>(() => SampleConfig.Parse(broken));
            Assert.DoesNotContain("TajneHaslo", error.Message);
        }
    }

    [Fact]
    public void LoadHandlesMissingFileBomAndInvalidUtf8()
    {
        var directory = Path.Combine(Path.GetTempPath(), "librus-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "librus_config.txt");
            Assert.Contains("brak pliku", Assert.Throws<ConfigException>(() => ConfigParser.Load(path)).Message);

            File.WriteAllText(path, SampleConfig.Text.Replace("dolnoslaskie", "dolnośląskie"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Assert.Equal("PL-DS", ConfigParser.Load(path).Voivodeship.Code);

            File.WriteAllBytes(path, [.. Encoding.UTF8.GetBytes("[rodzic]\nlogin="), 0xFF, 0xFE, (byte)'\n']);
            Assert.Contains("UTF-8", Assert.Throws<ConfigException>(() => ConfigParser.Load(path)).Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsWindowsLineEndings()
    {
        var config = SampleConfig.Parse(SampleConfig.Text.ReplaceLineEndings("\r\n"));
        Assert.Equal("HASLO_UCZNIA", config.Student.Password);
    }
}
