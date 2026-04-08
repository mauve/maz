# Custom Azure AD Application Registration

By default maz uses two Microsoft-owned app registrations:

| Purpose | Client ID | App |
|---|---|---|
| Azure Resource Manager (ARM) | `04b07795-8ddb-461a-bbee-02f9e1bf7b46` | Azure CLI |
| Microsoft Graph PIM scopes | `14d82eec-204b-4c2f-b7e8-296a70dab67e` | Microsoft Graph Command Line Tools |

Because these are different client IDs, a browser sign-in for one cannot produce a token
for the other, so `maz pim list` (and related commands) require **two** browser pop-ups after
a fresh `maz logout`.

Registering your own app with both permission sets reduces this to **one** pop-up and lets
you restrict which users and tenants can authenticate.

---

## Required API permissions

Create a new app registration in [Microsoft Entra ID](https://entra.microsoft.com) and grant
the following **delegated** permissions:

| API | Permission | Purpose |
|---|---|---|
| Azure Service Management | `user_impersonation` | ARM calls (role eligibility, subscriptions, etc.) |
| Microsoft Graph | `RoleManagement.ReadWrite.Directory` | Entra directory role PIM |
| Microsoft Graph | `PrivilegedAccess.ReadWrite.AzureADGroup` | Privileged group PIM |
| Microsoft Graph | `Directory.Read.All` | Resolving group/principal display names |

> **Admin consent** is required for the Graph permissions. A Global Administrator or
> Privileged Role Administrator must grant consent for your tenant.

---

## Step-by-step setup

### 1. Create the app registration

```
Entra ID → App registrations → New registration
  Name:          maz (or any name you prefer)
  Account types: Accounts in this organizational directory only
  Redirect URI:  Public client/native  →  http://localhost
```

### 2. Enable public client flows

```
Authentication → Advanced settings
  Allow public client flows: Yes
```

### 3. Add API permissions

```
API permissions → Add a permission

  Azure Service Management → Delegated → user_impersonation
  Microsoft Graph          → Delegated → RoleManagement.ReadWrite.Directory
  Microsoft Graph          → Delegated → PrivilegedAccess.ReadWrite.AzureADGroup
  Microsoft Graph          → Delegated → Directory.Read.All

  → Grant admin consent for <your tenant>
```

### 4. Note the Application (client) ID

Copy the **Application (client) ID** GUID from the app's Overview page.

---

## Configure maz to use the custom app

### Option A — interactive wizard

```
maz configure
```

At step 6 (Custom Azure AD Application), paste the client ID.

### Option B — edit the config file directly

Add to your `user-config.ini` under `[global]`:

```ini
[global]
auth-client-id = <your-client-id-guid>
```

The config file is at:
- **Linux/macOS**: `~/.config/maz/user-config.ini`
- **Windows**: `%APPDATA%\maz\user-config.ini`

### Option C — environment variable or CLI flag

```bash
export AZURE_CLIENT_ID=<your-client-id-guid>
# or per-invocation:
maz pim list --auth-client-id <your-client-id-guid>
```

---

## Result

With the custom app configured, all maz commands (including PIM) share a single client ID.
After `maz logout`, the next `maz pim list` (or any other command) will open **one** browser
window that covers ARM and all Graph PIM scopes simultaneously.
