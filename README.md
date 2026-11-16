# Winche.Core

What every Winche package shares: **settings** the console (or the CLI, or your own tools) can change while the app
runs, **secrets**, the **error** every Winche API answers with, and the **values** documents hold.

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
the code's values. Secrets (client secrets, storage keys, mail passwords) are kept encrypted with the project's master
key and are never shown again. Everything lives in the project's own PostgreSQL database, so a self-hosted project
carries its settings with it.

Each Winche package works without this one: without `AddWincheConfig`, its areas simply use the code's values.

```csharp
builder.Services.AddWincheConfig(o =>
{
    o.ConnectionString = connectionString;
    o.MasterKey = builder.Configuration["Winche:MasterKey"];   // 32+ characters; needed only for secrets
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
| `SecretStore.SetAsync / GetAsync / InfoAsync / DeleteAsync` | encrypted secrets (AES-256-GCM, bound to their name) |

Tables: `winche_settings`, `winche_secrets` (made on start, with `WincheSchema`: each package's tables are made and
upgraded in numbered steps under a lock, so many servers starting at once don't race). Other instances look for changes every `PollInterval`
(5 seconds). A saved version the app can no longer read (its shape changed) is logged and skipped.
