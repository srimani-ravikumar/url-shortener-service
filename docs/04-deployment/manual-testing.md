# Manual testing guide

How to start the whole system on your machine and try every feature by hand, scenario by scenario. Everything runs locally in Docker; the only thing you need is a browser.

## 1. Start everything

```powershell
./scripts/Start-Dev.ps1 -Build        # first time (builds the images); later: ./scripts/Start-Dev.ps1
```

It waits until every container is healthy, then prints the addresses:

| What | Address |
|---|---|
| **The web app** (use this for all scenarios) | http://localhost:8080/app/ |
| **Mail inbox** (MailPit: every mail the app sends lands here) | http://localhost:8025 |
| Short links | http://localhost:8080/{code} |
| Grafana, only with `-Observability` | http://localhost:3000 (admin / admin) |

Stop with `./scripts/Stop-Dev.ps1` (add `-Purge` to also delete all data).

## 2. Where do the mails go?

Sign-up and recovery send an email with a link. You choose where it is delivered.

### Option A: MailPit (default, nothing to set up)

Use any address you like, for example `me@example.com`. Open http://localhost:8025, click the mail, click the link inside. Nothing leaves your machine. Use the bin icon to clear the inbox between tests.

### Option B: your real inbox (Gmail or Brevo, free)

The mail then arrives in a real mailbox, which also tests how it looks and whether it lands in spam. Two limits to know:
- The link inside the mail always points to `http://localhost:8080`, so **open the mail on this same computer** (a phone cannot reach it).
- Providers replace the sender with the account's own verified address, so the mail will not come from `no-reply@trysmartly.co`.

**Never paste a password into a chat or a committed file.** Put it only in `deploy/.env`, which Git ignores.

1. Create the credentials.
   - **Gmail:** turn on 2-Step Verification for your Google account, then create an *app password* (Google Account, Security, App passwords). The username is your full Gmail address.
   - **Brevo:** create a free account, verify a sender address, then create an *SMTP key* (SMTP and API section). The username is your Brevo login email and the password is the SMTP key (not your login password).
2. Edit `deploy/.env` (create it by running `./scripts/Start-Dev.ps1` once, or copy `deploy/.env.example`) and fill the `SMTP_*` lines. The host names below are the providers' usual ones; check their current documentation if it fails.

   ```
   # Gmail
   SMTP_HOST=smtp.gmail.com
   SMTP_PORT=587
   SMTP_USE_STARTTLS=true
   SMTP_USERNAME=you@gmail.com
   SMTP_PASSWORD=<the app password>
   SMTP_FROM=you@gmail.com

   # Brevo
   SMTP_HOST=smtp-relay.brevo.com
   SMTP_PORT=587
   SMTP_USE_STARTTLS=true
   SMTP_USERNAME=<your Brevo login email>
   SMTP_PASSWORD=<the SMTP key>
   SMTP_FROM=<a sender you verified in Brevo>
   ```
3. Apply it: `docker compose -f deploy/docker-compose.yml up -d shortener-api`
4. Test: sign up in the app with **your own** address (scenario 1). The mail should arrive within a minute (look in spam too).
5. If the app says "The email could not be sent", read the reason: `docker compose -f deploy/docker-compose.yml logs --tail 30 shortener-api`. Usual causes: a wrong app password or SMTP key, an unverified sender, or the provider's daily limit.
6. Go back to MailPit: empty all `SMTP_*` lines in `deploy/.env` and run step 3 again.

## 3. Scenarios

Each scenario starts from the state described. Unless it says otherwise, use a private/incognito window for a "second user".

### 1. Sign up and create a first link
1. Open http://localhost:8080/app/. You see **Sign in**. Click **Get one**.
2. Enter your email, press **Send link**. You see **Check your inbox**.
3. Open the mail (MailPit or your inbox): subject *Confirm your email for trysmartly.co*. Click the link.
4. The page says **You are signed in** and shows your API key once. Press **Copy key** and keep it for later scenarios.
5. Press **I have saved my key, continue**. You land on **My links**; the header shows your email.
6. Enter `https://example.com/hello` and press **Shorten**. A short link appears and is listed under **Your links**.
7. Click the short link. It opens `https://example.com/hello`.

### 2. A mailed link works only once
1. Open the same mail link again (or press back and reload).
2. Expected: **This link does not work**, with links to get a new one.
3. Also note: after the first click the token disappears from the address bar.

### 3. Sign out and sign in with the key
1. Press **Sign out** (back on **Sign in**).
2. Paste a wrong key, press **Sign in**: *That key is not valid.*
3. Press **Sign in** with an empty field: *Enter your API key.*
4. Paste your real key: you reach **My links** with your earlier link.

### 4. Shorten options and errors
On **My links**:
1. **Custom alias:** `summer-sale` works and the link is `http://localhost:8080/summer-sale`.
2. The same alias again: *That custom alias is already in use.*
3. Alias `admin` (or `app`, `signup`): error that it is reserved. `ab`: too short. `bad alias!`: invalid characters.
4. Long URL `javascript:alert(1)` or `not a url`: an error appears under the field and nothing is created.
5. **Expiry:** set a date in the past: error under *Expires*. Set a time about 2 minutes ahead: the link works now; after it passes, opening it shows **410** and, after reloading **My links**, the entry has an **Expired** badge.

### 5. Click statistics
1. Open one of your short links three times (a new tab each time).
2. On **My links** press **Clicks** next to it. After about 10 seconds (the worker's flush interval) press **Refresh**: **Total clicks** is 3 and **Last followed** has a time.
3. A link nobody opened shows 0 and **Never**.

### 6. Many links and "Load more"
The list shows 20 at a time. To get more than 20 quickly, use a seeded user instead of your own account:
```powershell
./scripts/New-DevUser.ps1 -Count 1        # prints the new user's email and api_key
./scripts/New-DevLinks.ps1 -Count 30 -User <that email>
```
Sign in with that `api_key` and check **My links**: 20 entries, newest first, then **Load more** adds the rest.

### 7. Lost key: recovery
1. Sign out. On **Sign in** click **Get a new one**.
2. Enter your account's email, press **Send link**. You see **Check your inbox**.
3. Open the mail *Get a new API key for trysmartly.co* and click the link: a new key is shown once.
4. Try to sign in with the old key: *not valid*. The new key works.
5. Repeat step 2 with an address that has no account: the screen is identical, but **no mail arrives**. This is deliberate (nobody can find out who has an account).

### 8. Signing up with an address that already has an account
1. Sign out, **Get one**, enter the address of your existing account.
2. The screen is the same *Check your inbox*. The mail is *You already have an account on trysmartly.co* with a link to recovery, and no sign-up link.

### 9. Replace the key
1. Signed in, open **Account**, press **Replace key**. It warns that the current key stops working immediately. Press **Replace key** again (or **Cancel** to back out).
2. The new key is shown once. You stay signed in.
3. In a private window, sign in with the **old** key: not valid. The new key works.

### 10. One mail per minute
1. Sign up twice with the same new address within a minute.
2. Both show *Check your inbox*, but only **one** mail arrives.
3. After a minute, sign up again: a second mail arrives.

### 11. Delete the account
1. Create two links and open one of them (it redirects).
2. **Account**: the delete button is disabled. Type your email in full; it becomes enabled. Press **Delete my account**.
3. You return to **Sign in** with *Your account and all its links have been deleted.*
4. Open both short links: **404**. The old key is not valid.
5. Sign up again with the same address: it works and the new account has no links.

### 12. Mail server unavailable
1. `docker stop urlshortener-mailpit`
2. Sign up with a new address: a red message *The email could not be sent. Please try again shortly.*
3. `docker start urlshortener-mailpit`, wait a few seconds, try again: it works.

### 13. Bad input
1. Sign up with `nope`: *email is not a valid address.* under the field.
2. Open `http://localhost:8080/app/verify` (no token): **This link does not work**.

### 14. Page access
1. Signed out, open http://localhost:8080/app/links or `/app/account`: you are sent to **Sign in**.
2. Open `http://localhost:8080/app/does/not/exist`: you end on **Sign in** (or **My links** when signed in).
3. Signed in, open http://localhost:8080/app/: you land on **My links**.
4. Reload any page of the app (for example a statistics page): it loads again, still signed in.
5. `http://localhost:8080/app` (no trailing slash) redirects to `/app/`.

### 15. The key is not kept after the tab closes
1. Sign in, then close the tab and open http://localhost:8080/app/links in a new tab.
2. You are signed out (the key lives only in the tab's session storage).

### 16. Hourly request limit (optional)
1. In `deploy/.env` add `ACCOUNT_MAIL_REQUESTS_PER_HOUR=3`, then `docker compose -f deploy/docker-compose.yml up -d shortener-api`.
2. Request sign-up mails for 4 different addresses in a row. The fourth shows *You are sending requests too quickly. Wait a moment and try again.*
3. Remove the line and run the `up -d shortener-api` command again (it also resets the counter).

### 17. Whole-system regression (automatic)
```powershell
./scripts/Invoke-Scenario.ps1 -All
```
Runs about 85 checks against the running stack (sign-up, recovery, key replacement, deletion, expiry, rate limit, caching and more) and ends with *All checks passed.*

## 4. Handy commands

| Need | Command |
|---|---|
| Logs of a service | `docker compose -f deploy/docker-compose.yml logs --tail 50 shortener-api` |
| All containers and health | `docker ps --format "table {{.Names}}\t{{.Status}}"` |
| Restart after changing code | `./scripts/Start-Dev.ps1 -Build` |
| Grafana dashboards (traces, metrics, logs) | `./scripts/Start-Dev.ps1 -Observability` then http://localhost:3000 |
| Look at a stored row | `docker exec urlshortener-cassandra cqlsh -k urlshortener -e "SELECT email, user_id FROM users"` |
