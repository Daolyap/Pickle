namespace Pickle.Windows.Tests.Winget;

/// <summary>winget.exe output as captured through a redirected pipe (spinner noise, CRLF, truncation, CJK). Generated with exact column padding.</summary>
internal static class WingetFixtures
{
    public const string List =
        "   - \r   \\ \r   | \r   / \r                                        \rName                                     Id                                    Version          Available        Source\r\n-------------------------------------------------------------------------------------------------------------------------\r\nGit                                      Git.Git                               2.45.1           2.46.0           winget\r\nMicrosoft Visual C++ 2015-2022 Redistri… Microsoft.VCRedist.2015+.x64          14.38.33135.0    14.40.33810.0    winget\r\nPowerShell 7-x64                         Microsoft.PowerShell                  7.4.5.0                           winget\r\nWindows Terminal                         Microsoft.WindowsTerminal             1.20.11781.0                      winget\r\nGit (local)                              ARP\\Machine\\X64\\Git_is1               2.45.1\r\nContoso Very Long Package Identifier     Contoso.VeryLongPackageIdentifierTha… 3.0                               winget\r\n";

    public const string Upgrade =
        "  ██████████████████████████████  1.00 MB / 2.00 MB\r   - \r   \\ \r   | \r   / \r                                        \rName               Id                     Version   Available Source\n----------------------------------------------------------------------\nGit                Git.Git                2.45.1    2.46.0    winget\nPowerShell 7-x64   Microsoft.PowerShell   7.4.2.0   7.4.5.0   winget\n2 upgrades available.\n\nThe following packages have an upgrade available, but require explicit targeting for upgrade:\nName    Id              Version Available Source\n--------------------------------------------------\nSpotify Spotify.Spotify 1.2.3   1.2.4     winget\n";

    public const string Search =
        "\u001b[0m   - \r   \\ \r   | \r   / \r                                        \rName           Id                                    Version  Match        Source\n-----------------------------------------------------------------------------------\n微信           Tencent.WeChat                        3.9.10   Tag: wechat  winget\nQQ音乐         Tencent.QQMusic                       19.51                 winget\nVisual Studio… Microsoft.VisualStudio.2022.Community 17.11.4               winget\n";
}
