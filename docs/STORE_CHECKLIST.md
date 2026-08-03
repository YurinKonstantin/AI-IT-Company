# Microsoft Store checklist — AI IT-Company

Technical packaging is prepared in-repo. Publication steps require your Partner Center account.

## Capabilities (justify in Store questionnaire)

| Capability | Why |
|------------|-----|
| `runFullTrust` | Runs `dotnet` / git, writes project trees under the user profile, opens Explorer, uses Windows Credential Locker for API keys |
| `systemAIModels` | Optional Windows on-device AI models |

## In Partner Center (you)

1. **Reserve the app name** (Products → Create new app → reserve “AI IT-Company” or chosen name).
2. **Associate** the Visual Studio / WinUI project with the Store app (Project → Publish → Associate App with the Store). This rewrites `Package.appxmanifest` `Identity` / `Publisher` to your account CN.
3. Create a **submission** with listings for **en-US**, **ru-RU**, **zh-Hans**.
4. Provide **privacy policy URL** and **support contact**.
5. Complete **age ratings**, **windows capabilities declarations**, screenshots (Agent workspace, Review, Settings).
6. Upload MSIX / msixbundle from **Package and Publish** (x64 required; optionally x86/ARM64).

## In repository (done / maintain)

- [x] Single-project MSIX (`EnableMsixTooling`)
- [x] Store logo / tile assets under `AI IT-Company/Assets/`
- [x] Manifest languages: `en-US`, `ru-RU`, `zh-Hans`
- [x] Description text on VisualElements
- [x] `PublishSingleFile=false` for MSIX-friendly publish
- [ ] After association: commit updated Identity from Partner Center
- [ ] Bump `Identity Version` (e.g. `1.0.1.0`) on each Store flight — last segment is revision

## Versioning

`Package.appxmanifest` → `Identity Version="Major.Minor.Build.Revision"`.

Increment before every Store upload. Document release notes in the submission.

## Screenshots (suggested)

1. Agent workspace (timeline + composer + side panel)
2. Review (diff Apply/Reject)
3. Settings → Providers
4. Agents & Models

Capture at 1366×768 or higher; Store prefers 16:9.

## Privacy / support placeholders

Replace with your URLs before submission:

- Privacy: `https://example.com/ai-it-company/privacy`
- Support: `https://example.com/ai-it-company/support` or a support email

## Local package test

```powershell
dotnet publish "AI IT-Company\AI IT-Company.csproj" -c Release -p:Platform=x64 -p:WindowsPackageType=None
```

For Store package use Visual Studio **Create App Packages** / Partner Center upload after association.
