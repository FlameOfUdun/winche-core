# Winche.Core

What every Winche package shares: **settings** the console (or the CLI, or your own tools) can change while the app
runs, the **master key** signing keys derive from, the **error** every Winche API answers with, and the **values**
documents hold. (Secrets are Winche.Secrets', below: released with it, kept with the master key in the same database.)

## Install

```sh
dotnet add package Winche.Core
dotnet add package Winche.Core.AspNetCore   # for ASP.NET Core: errors as HTTP answers, the caller, the app setup endpoint
```

Every Winche package depends on Winche.Core, so an app using them already has it: add it yourself only to use it
directly.

## Value types

Each type has one name, written out in full, and every place derives from it: the wire key (`<name>Value`), the class
in C# and Dart (`<Name>Value`), the type in rules (`x is <name>`) and the console's label.

| Name | Wire | Class | Holds |
|---|---|---|---|
| `null` | `{"nullValue": null}` | `NullValue` | nothing |
| `boolean` | `{"booleanValue": true}` | `BooleanValue` | true or false |
| `integer` | `{"integerValue": "42"}` | `IntegerValue` | a 64-bit whole number (a string on the wire, so no digits are lost) |
| `double` | `{"doubleValue": 4.5}` | `DoubleValue` | a 64-bit floating-point number |
| `timestamp` | `{"timestampValue": "2026-11-15T10:00:00Z"}` | `TimestampValue` | a moment, to the microsecond |
| `string` | `{"stringValue": "Ann"}` | `StringValue` | text |
| `bytes` | `{"bytesValue": "AQID"}` | `BytesValue` | binary data (base64) |
| `documentReference` | `{"documentReferenceValue": "users/u1"}` | `DocumentReferenceValue` | a document, by its path |
| `fileReference` | `{"fileReferenceValue": "scans/a.png"}` | `FileReferenceValue` | a file in Winche Storage, by its path |
| `userReference` | `{"userReferenceValue": "u1"}` | `UserReferenceValue` | a Winche Auth user, by uid |
| `geoPoint` | `{"geoPointValue": {"latitude": 51.5, "longitude": -0.12}}` | `GeoPointValue` | a place |
| `array` | `{"arrayValue": {"values": [ … ]}}` | `ArrayValue` | an ordered list of values |
| `map` | `{"mapValue": {"fields": { … }}}` | `MapValue` | named values |

Rules also have `number` (an `integer` or a `double`). A path built in a rule, such as `/users/$(id)` or
`path('users', id)`, is a `documentReference`.
Values of different types sort in the order of the table (numbers together), the same in queries, indexes and rules.

## Errors

`WincheError` is the body of every failed Winche request — `{ "error": { "code", "message", "reason"?, "details"? } }`
— and `WincheErrorCode` the one list of codes (`not-found`, `permission-denied`, `unauthenticated`, …),
each with one HTTP status (`WincheError.StatusOf`). `reason` is an API's own, more precise code where callers need it
(`INVALID_CREDENTIALS`, `invalid-query`).

```csharp
var error = new WincheError(WincheErrorCode.NotFound, "There's no file at a/b.");
return Results.Json(error.ToBody(), statusCode: error.HttpStatus);
```

## Settings

The app's code gives each group of settings — an **area** — its starting values. A version saved in the store replaces
them on every instance within seconds, keeps a complete history (who, when, why), can be restored, and can be reset to
the code's values. Everything lives in the project's own PostgreSQL database, so a self-hosted project
carries its settings with it.

Each Winche package works without this one: without `AddWincheConfig`, its areas simply use the code's values.

```csharp
builder.Services.AddWincheConfig(o =>
{
    o.ConnectionString = connectionString;
    o.MasterKey = builder.Configuration["Winche:MasterKey"];   // 32+ characters: signing keys, and Winche.Secrets
});

// Packages register their areas (e.g. Winche.Auth's AddWincheTokens(t => t.UseSettings())); your own code can too:
var area = builder.Services.AddWincheSettingsArea("app.banner", "Banner", new Banner("Welcome"), b => b.Text.Length > 200 ? ["Too long."] : []);
// … later: area.Current, area.Changed += …
```

| | |
|---|---|
| `SettingsStore.SaveAsync(area, json, by, reason)` | checks the document against the area, saves a new version, applies it here at once |
| `SaveAsync(area, json, by, reason, basedOn)` | the same, made from version `basedOn` (0: none saved yet): refused with `SettingsConflictException` (its `Latest`) when someone saved another since, so their change isn't undone |
| `ResetAsync(area, …)` | back to the code's values (a version of its own, so it can be undone) |
| `RestoreAsync(id, …)` | an earlier version again, as a new version |
| `HistoryAsync(area?)` | every change, newest first |

Table: `winche_settings` (made on start, with `WincheSchema`: each package's tables are made and
upgraded in numbered steps under a lock, so many servers starting at once don't race). Other instances look for changes every `PollInterval`
(5 seconds). A saved version the app can no longer read (its shape changed) is logged and skipped.

## Winche.Secrets (and Winche.Secrets.Aws)

Released with Winche.Core, as packages of their own.

The secrets of a Winche app or server: passwords, API keys and client secrets (a Keycloak sync's, an SMTP server's, a
storage bucket's, functions' own). Each is either:

- a **value**, encrypted with the master key (AES-256-GCM, bound to its name) in the project's PostgreSQL database
  (`winche_secrets`), beside Winche.Core's settings; or
- a **reference** to where it really lives, e.g. AWS Secrets Manager or Parameter Store, read when it's needed and kept
  for a few minutes (so a rotation there is picked up).

Code reads either the same way: `await secrets.GetAsync("smtp.password")`. The console sets, replaces and removes
secrets; it never reads them back.

```csharp
builder.Services.AddWincheConfig(o => { o.ConnectionString = conn; o.MasterKey = config["Winche:MasterKey"]; });
builder.Services.AddWincheSecrets(o => o.CacheFor = TimeSpan.FromMinutes(5));   // packages that keep secrets add it themselves too
builder.Services.AddWincheSecretsAws();                                          // Winche.Secrets.Aws: aws-sm and aws-ssm references
```

### References

Written `source:locator[#field]`:

| | |
| --- | --- |
| `aws-sm:arn:aws:secretsmanager:eu-west-2:123456789012:secret:app/ses-AbCdEf#secretAccessKey` | a field of a JSON secret in AWS Secrets Manager (the ARN gives the region) |
| `aws-sm:app/ses` | the whole secret, by name (the region from the project's key or the app's default) |
| `aws-ssm:/app/keycloak/clientSecret` | a Parameter Store parameter (SecureStrings decrypted) |

```csharp
await secrets.SetReferenceAsync("functions.secret.SES_SECRET", SecretReference.Parse("aws-sm:app/ses#secretAccessKey"), by, allowAppCredentials: true);
var check = await secrets.TestAsync("functions.secret.SES_SECRET");   // { Ok, Problem }: never the value
```

**Credentials.** A source reads with the project's own key when it keeps one (AWS: the secret `secrets.source.aws`,
`{"accessKeyId", "secretAccessKey", "region"?}`), else with the app's own credentials (its role, environment or
profile). On a host serving several projects the app's credentials could read anything granted to the host, so
`SetReferenceAsync` refuses them unless `allowAppCredentials` (Winche Server: server admins only). A reference set with
the project's key is never read with the app's credentials later, even if the key is removed.

Other sources implement `ISecretSource` and register with `services.AddWincheSecretSource<T>()`.

### Changing the master key

Deploy the new one as `MasterKey` and the old one in `PreviousMasterKeys`; on start every value still under the old
key is re-encrypted with the new one. References have nothing to re-encrypt.
