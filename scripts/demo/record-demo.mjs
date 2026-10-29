// Records an end-to-end demo of the local stack as an mp4: sign-up by mail, links, statistics, recovery,
// key replacement, mail outage and account deletion. Every step is checked, so the video doubles as proof.
//
// Needs the stack running (./scripts/Start-Dev.ps1) and Microsoft Edge or Chrome installed.
// Usage: cd scripts/demo; npm install; npm run record      (output: scripts/demo/output/demo.mp4)
import { chromium } from 'playwright';
import ffmpegPath from 'ffmpeg-static';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const APP = process.env.DEMO_BASE_URL ?? 'http://localhost:8080';
const MAIL = process.env.DEMO_MAIL_URL ?? 'http://localhost:8025';
const OUT = path.join(path.dirname(fileURLToPath(import.meta.url)), 'output');
const SIZE = { width: 1280, height: 720 };

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const stamp = Date.now().toString(36);
const email = `demo-${stamp}@example.com`;
const downEmail = `demo-down-${stamp}@example.com`;
const alias = `demo-${stamp}`; // unique per run, so click counts never depend on an earlier run
const results = [];
let caption = '';

// ---------------------------------------------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------------------------------------------
function check(name, ok, detail = '') {
  results.push({ name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? ' - ' + detail : ''}`);
}

function docker(...args) {
  execFileSync('docker', args, { stdio: 'pipe' });
}

async function launchBrowser() {
  for (const channel of ['msedge', 'chrome', undefined]) {
    try {
      return await chromium.launch({ channel, headless: true });
    } catch {
      // try the next one
    }
  }
  throw new Error('No browser found. Install Microsoft Edge or Chrome, or run: npx playwright install chromium');
}

async function waitFor(fn, timeoutMs, everyMs = 1000) {
  const end = Date.now() + timeoutMs;
  while (Date.now() < end) {
    const value = await fn();
    if (value) return value;
    await sleep(everyMs);
  }
  return null;
}

// ---------------------------------------------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------------------------------------------
fs.rmSync(OUT, { recursive: true, force: true });
fs.mkdirSync(OUT, { recursive: true });

const browser = await launchBrowser();
const context = await browser.newContext({
  viewport: SIZE,
  recordVideo: { dir: OUT, size: SIZE },
  locale: 'en-US',
});
const page = await context.newPage();
const video = page.video();

// A caption bar (bottom left, clear of the app's toasts) that survives page navigations: every page
// asks the Node side for the current text a few times a second.
await context.exposeFunction('__caption', () => caption);
await context.addInitScript(() => {
  if (window.top !== window) return;
  const render = async () => {
    try {
      const text = await window.__caption();
      let el = document.getElementById('__demo_caption');
      if (!text) {
        el?.remove();
        return;
      }
      if (!el) {
        el = document.createElement('div');
        el.id = '__demo_caption';
        el.style.cssText =
          'position:fixed;left:16px;bottom:16px;max-width:calc(100% - 440px);z-index:2147483647;' +
          'background:rgba(17,24,39,.93);color:#fff;font:600 21px/1.35 system-ui,sans-serif;' +
          'padding:12px 20px;border-radius:10px;pointer-events:none';
        (document.body ?? document.documentElement).appendChild(el);
      }
      if (el.textContent !== text) el.textContent = text;
    } catch {
      // the page is going away
    }
  };
  setInterval(render, 250);
});

const say = async (text, ms = 1500) => {
  caption = text;
  await sleep(ms);
};
const typeInto = (selector, text) => page.locator(selector).pressSequentially(text, { delay: 45 });
const bodyText = async () => ((await page.locator('body').innerText()) ?? '').replace(/\s+/g, ' ');

async function findMail(address, subjectPart) {
  const q = encodeURIComponent(`to:${address}`);
  return waitFor(async () => {
    const found = await (await context.request.get(`${MAIL}/api/v1/search?query=${q}`)).json();
    const hit = (found.messages ?? []).find((m) => m.Subject.includes(subjectPart));
    if (!hit) return null;
    const full = await (await context.request.get(`${MAIL}/api/v1/message/${hit.ID}`)).json();
    return { id: hit.ID, subject: hit.Subject, link: /(https?:\/\/\S+#token=\S+)/.exec(full.Text)?.[1] ?? null };
  }, 20000);
}

async function showMail(mail) {
  await page.goto(`${MAIL}/view/${mail.id}`);
  await page.waitForTimeout(3500);
}

async function card(html, ms) {
  caption = '';
  await page.setContent(
    `<body style="margin:0;background:#111827;color:#fff;font-family:system-ui,sans-serif;display:flex;
      align-items:center;justify-content:center;height:100vh"><div style="max-width:1100px;padding:0 40px">${html}</div></body>`,
  );
  await sleep(ms);
}

async function signIn(key) {
  await page.goto(`${APP}/app/`);
  await typeInto('#api-key', key);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

try {
  // Stack must be up, and the inbox starts empty so the mails on screen are the ones this run sends.
  const ready = await context.request.get(`${APP}/health/ready`);
  if (!ready.ok()) throw new Error(`The stack is not ready at ${APP}. Start it with ./scripts/Start-Dev.ps1`);
  await context.request.delete(`${MAIL}/api/v1/messages`);

  await card(
    `<h1 style="font-size:44px;margin:0 0 16px">trysmartly.co</h1>
     <p style="font-size:26px;margin:0 0 24px;color:#cbd5e1">URL shortener - end-to-end demo on the local stack</p>
     <ol style="font-size:22px;line-height:1.7;color:#e5e7eb">
       <li>Sign up by email and get an API key</li><li>Create short links, aliases and errors</li>
       <li>Follow a link and see the click statistics</li><li>Lost key: recover by email</li>
       <li>Replace the key</li><li>Mail server down</li><li>Delete the account and all its links</li></ol>
     <p style="font-size:18px;color:#94a3b8">Real Cassandra, Redis, nginx, three services, Angular app. Mail goes to MailPit (local inbox).</p>`,
    6000,
  );

  // 1 ------------------------------------------------------------------------------------------------------------
  await page.goto(`${APP}/app/`);
  await say('1. Sign up: ask for an API key by email', 2000);
  await page.getByRole('link', { name: 'Get one' }).click();
  await typeInto('#email', email);
  await page.getByRole('button', { name: 'Send link' }).click();
  await page.getByRole('heading', { name: 'Check your inbox' }).waitFor();
  const signupAt = Date.now();
  check('sign-up shows "Check your inbox"', true);
  await say('The app mailed a verification link', 1800);

  const signupMail = await findMail(email, 'Confirm your email');
  check('verification mail arrives in the inbox', !!signupMail?.link);
  await say('MailPit, the local inbox: the mail with the link', 500);
  await showMail(signupMail);
  await say('Open the link from the mail', 1800);
  await page.goto(signupMail.link);
  await page.locator('[data-testid=api-key]').waitFor();
  const firstKey = (await page.locator('[data-testid=api-key]').innerText()).trim();
  check('API key is shown once (43 characters)', firstKey.length === 43);
  check('mail link token is removed from the address bar', !page.url().includes('token='), page.url());
  await say('Signed in. The key is shown only this once', 3500);
  await page.getByRole('button', { name: 'I have saved my key, continue' }).click();
  await page.getByRole('heading', { name: 'My links' }).waitFor();

  // 2 ------------------------------------------------------------------------------------------------------------
  await say('2. Create a short link with a custom alias', 1800);
  await typeInto('#long-url', 'https://example.com/trysmartly/demo-page');
  await typeInto('#custom-alias', alias);
  await page.getByRole('button', { name: 'Shorten' }).click();
  await page.locator('.result a').waitFor();
  const shortUrl = (await page.locator('.result a').innerText()).trim();
  check('short link created with the chosen alias', shortUrl.endsWith('/' + alias), shortUrl);
  await say(`Short link: ${shortUrl}`, 2500);

  await say('Same alias again: refused', 800);
  await typeInto('#long-url', 'https://example.com/another');
  await typeInto('#custom-alias', alias);
  await page.getByRole('button', { name: 'Shorten' }).click();
  await page.locator('#custom-alias-error').waitFor();
  check('duplicate alias is refused', /already in use/i.test(await page.locator('#custom-alias-error').innerText()));
  await sleep(2200);

  await say('Reserved word "admin": refused', 800);
  await page.locator('#custom-alias').fill('');
  await typeInto('#custom-alias', 'admin');
  await page.getByRole('button', { name: 'Shorten' }).click();
  await page.locator('#custom-alias-error:has-text("reserved")').waitFor();
  check('reserved alias is refused', true);
  await sleep(2200);

  // 3 ------------------------------------------------------------------------------------------------------------
  // The heading shows before the list has loaded, so wait for the entry itself.
  const linkListed = () =>
    page
      .locator('li.link', { hasText: alias })
      .waitFor({ timeout: 10000 })
      .then(() => true, () => false);
  await say('3. Follow the short link (the redirector answers 302)', 1500);
  const hit = await context.request.get(shortUrl, { maxRedirects: 0 });
  let clicks = 1;
  check('short link redirects (302) to the long URL', hit.status() === 302 && hit.headers()['location'] === 'https://example.com/trysmartly/demo-page');
  try {
    await page.goto(shortUrl, { timeout: 15000, waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
  } catch {
    // the target site may be unreachable from this machine; the redirect itself is what is checked
  }
  clicks += 1;
  for (let i = 0; i < 3; i++) {
    await context.request.get(shortUrl, { maxRedirects: 0 });
    clicks += 1;
  }

  await page.goto(`${APP}/app/links`);
  await page.getByRole('heading', { name: 'My links' }).waitFor();
  await say(`Opened the link ${clicks} times. Clicks are counted by a background worker (about 10 s)`, 2500);
  await page.getByRole('link', { name: 'Clicks' }).first().click();
  await page.locator('[data-testid=total-clicks]').waitFor();
  let counted = null;
  for (let i = 0; i < 10 && counted !== String(clicks); i++) {
    await sleep(4000);
    await page.getByRole('button', { name: 'Refresh' }).click();
    await page.waitForTimeout(600);
    counted = (await page.locator('[data-testid=total-clicks]').innerText()).trim();
  }
  check(`statistics show ${clicks} clicks`, counted === String(clicks), `shown: ${counted}`);
  await say(`Statistics: ${counted} clicks`, 3000);

  // 4 ------------------------------------------------------------------------------------------------------------
  await say('4. Lost the key? Recover by email (one mail per address per minute)', 1500);
  await page.getByRole('button', { name: 'Sign out' }).click();
  const wait = 62000 - (Date.now() - signupAt);
  if (wait > 0) await say('Waiting out the one-mail-per-minute cooldown for this address...', wait);
  await page.getByRole('link', { name: 'Get a new one' }).click();
  await typeInto('#email', email);
  await page.getByRole('button', { name: 'Send link' }).click();
  await page.getByRole('heading', { name: 'Check your inbox' }).waitFor();
  const recoverMail = await findMail(email, 'Get a new API key');
  check('recovery mail arrives', !!recoverMail?.link);
  await showMail(recoverMail);
  await say('Open the recovery link', 1500);
  await page.goto(recoverMail.link);
  await page.locator('[data-testid=api-key]').waitFor();
  const secondKey = (await page.locator('[data-testid=api-key]').innerText()).trim();
  check('recovery issues a different key', secondKey.length === 43 && secondKey !== firstKey);
  await say('A new key. The old one is revoked', 3000);

  await page.getByRole('button', { name: 'I have saved my key, continue' }).click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await say('Try to sign in with the OLD key', 1200);
  await signIn(firstKey);
  await page.locator('#api-key-error').waitFor();
  check('old key is rejected', /not valid/i.test(await page.locator('#api-key-error').innerText()));
  await sleep(2500);
  await say('Sign in with the NEW key', 1000);
  await page.locator('#api-key').fill('');
  await typeInto('#api-key', secondKey);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.getByRole('heading', { name: 'My links' }).waitFor();
  check('new key signs in and the earlier link is listed', await linkListed());
  await sleep(2000);

  // 5 ------------------------------------------------------------------------------------------------------------
  await say('5. Replace the key from the Account page', 1500);
  await page.getByRole('link', { name: 'Account' }).click();
  await page.getByRole('button', { name: 'Replace key' }).click();
  await say('It warns: the current key stops working at once', 2500);
  await page.locator('button.btn-primary', { hasText: 'Replace key' }).click();
  await page.locator('[data-testid=api-key]').waitFor();
  const thirdKey = (await page.locator('[data-testid=api-key]').innerText()).trim();
  check('key replacement issues a different key', thirdKey.length === 43 && thirdKey !== secondKey);
  await say('New key shown once; the session continues with it', 3500);
  await page.getByRole('link', { name: 'My links' }).click();
  await page.getByRole('heading', { name: 'My links' }).waitFor();
  check('session keeps working after replacing the key', await linkListed());

  // 6 ------------------------------------------------------------------------------------------------------------
  await say('6. Mail server down: sign-up reports it instead of failing silently', 1800);
  await page.getByRole('button', { name: 'Sign out' }).click();
  docker('stop', 'urlshortener-mailpit');
  try {
    await page.goto(`${APP}/app/signup`);
    await typeInto('#email', downEmail);
    await page.getByRole('button', { name: 'Send link' }).click();
    await page.locator('.toast.error').waitFor();
    check('mail outage is reported to the user', /could not be sent/i.test(await page.locator('.toast.error').innerText()));
    await sleep(3500);
  } finally {
    docker('start', 'urlshortener-mailpit');
  }
  await say('Mail server back up', 500);
  await waitFor(async () => {
    const r = await context.request.get(`${MAIL}/api/v1/info`).catch(() => null);
    return r?.ok();
  }, 30000);

  // 7 ------------------------------------------------------------------------------------------------------------
  await say('7. Delete the account: all its links go with it', 1800);
  await signIn(thirdKey);
  await page.getByRole('heading', { name: 'My links' }).waitFor();
  await page.getByRole('link', { name: 'Account' }).click();
  const deleteButton = page.getByRole('button', { name: 'Delete my account' });
  check('delete button is disabled until the email is typed', await deleteButton.isDisabled());
  await typeInto('#confirm-email', email);
  await sleep(800);
  await deleteButton.click();
  await page.locator('.toast.success').waitFor();
  check('account deleted and the user is signed out', (await bodyText()).includes('Sign in'));
  await say('Account and links deleted', 2500);

  await say('The short link is gone (404) and the old key no longer works', 1500);
  const gone = await page.goto(shortUrl);
  check('the deleted account\'s short link answers 404', gone?.status() === 404, `status ${gone?.status()}`);
  await sleep(2500);
  await signIn(thirdKey);
  await page.locator('#api-key-error').waitFor();
  check('the deleted account\'s key is rejected', /not valid/i.test(await page.locator('#api-key-error').innerText()));
  await sleep(2000);

  // 8 ------------------------------------------------------------------------------------------------------------
  await say('8. Pages that need a sign-in send visitors to the sign-in page', 1500);
  await page.goto(`${APP}/app/links`);
  await page.getByRole('heading', { name: 'Sign in' }).waitFor();
  check('signed-out visit to /app/links goes to sign-in', true);
  await sleep(2500);

  const failed = results.filter((r) => !r.ok);
  await card(
    `<h1 style="font-size:36px;margin:0 0 8px">${failed.length === 0 ? 'All checks passed' : failed.length + ' check(s) failed'}</h1>
     <p style="font-size:20px;color:#94a3b8;margin:0 0 16px">${results.length} checks in this recording</p>
     <ul style="font-size:17px;line-height:1.55;list-style:none;padding:0;margin:0;columns:2;column-gap:32px">
     ${results.map((r) => `<li style="break-inside:avoid">${r.ok ? '&#10003;' : '&#10007;'} ${r.name}</li>`).join('')}</ul>`,
    9000,
  );
} catch (error) {
  console.error('\nDemo stopped:', error.message);
  check('demo ran to the end', false, error.message);
} finally {
  await context.close();
  await browser.close();
}

// Playwright writes webm; convert it to a widely playable mp4.
const webm = await video.path();
const mp4 = path.join(OUT, 'demo.mp4');
execFileSync(ffmpegPath, ['-y', '-i', webm, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-preset', 'medium', '-crf', '23', '-movflags', '+faststart', mp4], { stdio: 'ignore' });
fs.rmSync(webm);
fs.writeFileSync(path.join(OUT, 'results.json'), JSON.stringify({ email, results }, null, 2));

const failedCount = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failedCount}/${results.length} checks passed. Video: ${mp4}`);
process.exit(failedCount === 0 ? 0 : 1);
