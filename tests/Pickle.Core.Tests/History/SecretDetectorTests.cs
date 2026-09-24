using Pickle.Core.History;

namespace Pickle.Core.Tests.History;

public class SecretDetectorTests
{
    [Theory]
    [InlineData("New-LocalUser bob -Password hunter2")]
    [InlineData("Connect-Service -Token 'abc'")]
    [InlineData("Set-Secret -Name x -Secret \"s3cr3t\"")]
    [InlineData("Invoke-Api -ApiKey:xyz")]
    [InlineData("Protect-Data -Key 0123456789abcdef")]
    [InlineData("Login -AccessToken abc")]
    [InlineData("Get-Thing -ClientSecret abc")]
    [InlineData("mytool --password=hunter2")]
    [InlineData("gh auth login --with-token abc")]
    [InlineData("mysql --api-key abc")]
    [InlineData("$pw = ConvertTo-SecureString 'P@ss' -AsPlainText -Force")]
    [InlineData("sqlcmd -Q x -ConnectionString 'Server=.;Database=db;User Id=sa;Password=hunter2;'")]
    [InlineData("$cs = 'Server=x;Uid=sa;Pwd=hunter2'")]
    [InlineData("curl -H 'Authorization: Bearer abc.def.ghi' https://api.example.com")]
    [InlineData("Invoke-RestMethod -Headers @{Authorization=\"Bearer eyJhbGciOiJIUzI1NiJ9\"} -Uri $u")]
    [InlineData("$env:AWS_ACCESS_KEY_ID = 'AKIAIOSFODNN7EXAMPLE'")]
    [InlineData("git clone https://ghp_abcdefghijklmnopqrstuvwxyz0123456789@github.com/o/r")]
    [InlineData("$t = 'github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz'")]
    [InlineData("Send-Slack -Hook x xoxb-123456789012-abcdefghij")]
    [InlineData("Set-Content key.pem '-----BEGIN RSA PRIVATE KEY-----'")]
    [InlineData("git remote add origin https://user:hunter2@example.com/repo.git")]
    [InlineData("curl -u admin:hunter2 https://example.com")]
    [InlineData("$apiKey = 'abcdef'")]
    [InlineData("$env:OPENAI_API_KEY='sk-proj-abcdefghijklmnopqrstuvwxyz'")]
    public void DetectsSecrets(string commandLine) => Assert.True(SecretDetector.ContainsSecret(commandLine), commandLine);

    [Theory]
    [InlineData("Get-ChildItem -Force")]
    [InlineData("git commit -m \"fix token refresh\"")]
    [InlineData("Get-Secret -Name github")]
    [InlineData("New-LocalUser bob -Password $securePassword")]
    [InlineData("Connect-Service -Token $env:TOKEN")]
    [InlineData("Connect-Service -Token (Get-Secret gh -AsPlainText)")]
    [InlineData("$password = Read-Host -AsSecureString")]
    [InlineData("Get-Content ./keys.txt")]
    [InlineData("Set-PSReadLineKeyHandler -Key Tab -Function Complete")]
    [InlineData("New-LocalUser bob -PasswordNeverExpires")]
    [InlineData("Get-Help about_Tokens")]
    [InlineData("Get-ChildItem Env:")]
    [InlineData("ssh -i ~/.ssh/id_ed25519 host")]
    [InlineData("")]
    public void AllowsOrdinaryCommands(string commandLine) => Assert.False(SecretDetector.ContainsSecret(commandLine), commandLine);
}
