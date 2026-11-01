# Vendored Inno Setup translations

Clink-Setup.exe is translated into the app's 16 languages. Inno Setup 6.7.3 (the version pinned in the workflows) ships official
files for 14 of them (`compiler:Languages\*.isl`). It ships none for Hindi or Simplified Chinese, so those two are vendored here
unmodified (byte-identical) and referenced by relative path from `installer\Clink.iss`.

| File | Language | Encoding |
| --- | --- | --- |
| `Hindi.islu` | Hindi (`hindi`) | UTF-8 with BOM |
| `ChineseSimplified.isl` | Simplified Chinese (`chinesesimplified`) | UTF-8, no BOM (as the official files) |

- Source: https://github.com/jrsoftware/issrc/tree/4adf37ed7f3fd2bd11c6836ba056e3de170fbabf/Files/Languages/Unofficial
  (commit 4adf37ed7f3fd2bd11c6836ba056e3de170fbabf = tag `is-6_7_3`, the version CI installs).
- SHA-256: `Hindi.islu` FBB1045F3B25842BB926BDD5400D07875F4C8572B04FFAB14BB7ADD9882CC19B,
  `ChineseSimplified.isl` 7D544B9BB1D142CFA11F2E5D3CC8ABE2E55F8E066C5124E3772675AA236E1278.
- Authors (from the file headers): Hindi by Him Prasad Gautam (written for Inno 5.5.3+, so messages added since fall back to
  English); Simplified Chinese by Zhenghan Yang (https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation).
- License: distributed in the Inno Setup source repository under the Inno Setup license (license.txt: use, alteration and
  redistribution allowed with the copyright notices kept; modified versions must be marked as such). The files are unmodified.
- Update: when the pinned Inno Setup version changes, re-download both from the matching tag and update this file.

## Intentional overrides (in `..\ClinkMessages.isi`, `[Messages]` section)

The vendored files stay unmodified; everything below is a `hindi.<Name>` / `brazilianportuguese.<Name>` override in `..\ClinkMessages.isi`. `tools\tests\installer-script.tests.ps1` checks them against Default.isl of the installed Inno Setup (`INNO_SETUP_DIR` or the standard folders; it prints `SKIP: Default.isl not found (set INNO_SETUP_DIR)` otherwise) and against an explicit allowlist: an override of a key the language file already defines must be on that allowlist.

- Hindi, missing keys: `Hindi.islu` was written for Inno Setup 5.5.3, so the built-in messages added since (downloads, extraction, archive errors, file-conflict and privilege dialogs, uninstall display names, signature verification) are translated as overrides. The test requires one for every non-empty message Default.isl has and `Hindi.islu` lacks.
- Hindi, main path (allowlisted): the file's wording and spelling are poor on the screens Clink shows, so the Welcome, Select Tasks, Ready, Preparing, Installing and Finished pages, the button captions, exit/language/application-in-use dialogs, status lines and uninstall texts are overridden. `WindowsVersionNotSupported` held the Welcome text and `InvalidParameter` held a folder-name message without `%1`; both are restored. Wording is consistent across all overrides: सेटअप, इंस्टॉल / इंस्टॉलेशन, अनइंस्टॉल, कंप्यूटर, फ़ोल्डर, स्टार्ट मेनू, शॉर्टकट. The Inno custom messages `AdditionalIcons`, `CreateDesktopIcon`, `ProgramOnTheWeb`, `UninstallProgram` and `LaunchProgram` are overridden as `hindi.<Name>` in `[CustomMessages]`. Accelerator letters inside one dialog are unique (checked by the test; `ButtonStopDownload` was `डाउनलोड &रोकें`, colliding with `&रद्द करें`).
- Hindi, `BeveledLabel`: emptied, because the file's credit line (सौजन्यः हिम प्रसाद गौतम) would otherwise show on every wizard page; the author credit stays in this README and in the file header.
- Brazilian Portuguese, `OnlyOnTheseArchitectures` (allowlisted): `BrazilianPortuguese.isl` 6.7.3 writes `% 1` instead of `%1`, so the architecture list would be missing from the message.

## Remaining harmless compiler warnings

ISCC still prints these for the vendored Hindi file and they are not failures (package.ps1 echoes warnings and fails only on `has not been defined for the "<language>" language`): nine `Warning:` lines, seven `Message name ... is not recognized by this version of Inno Setup. Ignoring.` (`EntryAbortRetryIgnore`, `FileAbortRetryIgnore`, `FileAbortRetryIgnore2`, `ExistingFileReadOnly`, `FileExists`, `ExistingFileNewer`, `MissingWOW64APIs`: messages Hindi.islu defines that 6.7.3 no longer knows) and two `[LangOptions] section directive ... is obsolete` (`TitleFontSize`, `CopyrightFontSize`). A compile without `/DDotNetRuntimeUrl` adds one hint, `Variable 'RUNTIMEDOWNLOADED' never used`. (The file's `AutoStartProgram` custom message holds a wrong text; Clink does not use it, so it is left alone.)

Clink's own installer texts (the .NET runtime task and its messages) are in `..\ClinkMessages.isi`.
