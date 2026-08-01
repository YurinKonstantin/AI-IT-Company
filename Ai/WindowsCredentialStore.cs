using System;
using Windows.Security.Credentials;

namespace Ai;

/// <summary>
/// Обёртка над Windows PasswordVault (Credential Locker).
/// API-ключи не хранятся в SQLite.
/// </summary>
public sealed class WindowsCredentialStore
{
    public const string OpenRouterResource = "AiItCompany/OpenRouter";
    private const string DefaultUserName = "api-key";

    public void SetSecret(string resource, string secret)
    {
        if (string.IsNullOrWhiteSpace(resource))
            throw new ArgumentException("Resource is required.", nameof(resource));
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Secret is required.", nameof(secret));

        var vault = new PasswordVault();
        RemoveSecret(resource);

        vault.Add(new PasswordCredential(resource, DefaultUserName, secret.Trim()));
    }

    public string? GetSecret(string resource)
    {
        try
        {
            var vault = new PasswordVault();
            var cred = vault.Retrieve(resource, DefaultUserName);
            cred.RetrievePassword();
            return string.IsNullOrWhiteSpace(cred.Password) ? null : cred.Password;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool HasSecret(string resource) => GetSecret(resource) is not null;

    public void RemoveSecret(string resource)
    {
        try
        {
            var vault = new PasswordVault();
            var existing = vault.FindAllByResource(resource);
            foreach (var c in existing)
            {
                try { vault.Remove(c); }
                catch { /* ignore */ }
            }
        }
        catch
        {
            /* nothing stored */
        }
    }
}
