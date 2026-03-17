#define MyAppName "AI Scraping Defense"
#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
#ifndef SourceDir
  #error SourceDir must be defined on the ISCC command line.
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#ifndef RuntimeIdentifier
  #define RuntimeIdentifier "win-x64"
#endif

#if RuntimeIdentifier == "win-arm64"
  #define ArchitectureIdentifier "arm64"
  #define OutputArchitectureSuffix "windows-arm64"
#else
  #define ArchitectureIdentifier "x64compatible"
  #define OutputArchitectureSuffix "windows-x64"
#endif

[Setup]
AppId={{8D1305F0-F4F8-4ED3-9A03-2F37FD13B8A4}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher=AI Scraping Defense Contributors
DefaultDirName={autopf}\AiScrapingDefense
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed={#ArchitectureIdentifier}
ArchitecturesInstallIn64BitMode={#ArchitectureIdentifier}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=ai-scraping-defense-{#AppVersion}-{#OutputArchitectureSuffix}-setup
UninstallDisplayIcon={app}\AiScrapingDefense.EdgeGateway.exe
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "installservice"; Description: "Install the Windows service"; Flags: checkedonce
Name: "startservice"; Description: "Start the Windows service after installation (requires valid production configuration)"; Flags: unchecked

[Dirs]
Name: "{app}\data"
Name: "{app}\logs"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\Install-WindowsService.ps1"" -InstallDir ""{app}"""; \
    Flags: runhidden waituntilterminated; \
    StatusMsg: "Installing Windows service..."; \
    Tasks: installservice
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\Install-WindowsService.ps1"" -InstallDir ""{app}"" -StartService"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Starting Windows service..."; \
  Tasks: startservice

[UninstallRun]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\Uninstall-WindowsService.ps1"""; \
    Flags: runhidden waituntilterminated