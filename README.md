# Batch Plugin Update — Rock Shop block

A drop-in Rock block that installs **all** selected Rock Shop package updates in **one pass**, with **one** store login and **one** application restart — instead of the stock one-plugin-at-a-time flow that re-prompts for your password and restarts Rock on every single update.

Built for exactly the situation this was written for: a big wave of plugin updates (the v18/v19 compatibility recompile) hitting all at once.

## Files

| File | Where it goes |
|------|----------------|
| `Plugins/org_ccbcfamily/Store/BatchPackageInstall.ascx` | `~/Plugins/org_ccbcfamily/Store/` |
| `Plugins/org_ccbcfamily/Store/BatchPackageInstall.ascx.cs` | `~/Plugins/org_ccbcfamily/Store/` (same folder) |

> Rename the `org_ccbcfamily` folder and the namespace if you prefer your own org code — just keep the folder name and the `namespace` line in the `.cs` in sync, and update the block path you register.

## How it works (the important part)

The two annoyances both come from how Rock's own installer behaves:

1. **Password every time** — each install calls `StoreService.Purchase(username, password, packageId)` against `store.rockrms.com`, and the stock UI runs that on a fresh page per plugin. This block collects your store login **once** and reuses it for every `Purchase()` call in the batch.

2. **Restart every time** — writing a DLL into `~/bin` forces ASP.NET to recycle the app domain. The stock flow drops each plugin's DLLs on its own request, so every plugin = its own restart. This block instead:
   - runs each package's `run.sql` and extracts all **non-`/bin`** content during the loop, and
   - **holds every `/bin` DLL write in a staging folder** (`~/App_Data/RockShop/_BatchStaging`), then
   - **flushes all the `/bin` writes in one tight loop at the very end** of the single postback.

   Because the only thing that triggers the recycle is the `/bin` write, and ASP.NET lets the in-flight request finish before unloading the app domain, the whole batch produces **exactly one restart** — after everything is staged and the database changes are already committed.

It deliberately reuses Rock core's own install primitives — XDT transforms, `run.sql`, `deletefile.lst`, the core-DLL blacklist, and `InstalledPackageService` bookkeeping — so each package installs identically to the built-in installer.

## Install / register the block

1. Copy both files to `~/Plugins/org_ccbcfamily/Store/` on the server (on your beta box first).
2. In Rock: **Admin Tools -> CMS Configuration -> Block Types -> +**
   - **Name:** Batch Plugin Update
   - **Path:** `~/Plugins/org_ccbcfamily/Store/BatchPackageInstall.ascx`
   - **Category:** CCBC > Store
3. Create (or pick) an internal admin page, edit it, and **add the "Batch Plugin Update" block** to a zone.
4. Lock the page/block down to your admin role (it can install code and run SQL — treat it like the Rock Shop pages).

No Visual Studio build is required: the `.ascx` uses `CodeFile`, so RockWeb compiles it at runtime. If your environment is configured for **pre-compiled blocks only**, compile the `.cs` into a small plugin DLL instead and change the `.ascx` header from `CodeFile=...` to `CodeBehind=...`.

## Using it

1. Open the page. It lists every purchased package that has a **newer version compatible with your current Rock version**.
2. Check the ones to update (all checked by default; use the header box to toggle all).
3. Enter your **Rock Store** username/password once, tick the acknowledgement, click **Install Selected Updates**.
4. It works through them, then shows a per-package results table and tells you Rock is restarting **once**. Wait ~30-60s and refresh.

## Before you run it (on prod)

- **Back up the database.** `run.sql` scripts execute real schema/data changes.
- **Run during a maintenance window** — there's still one restart, and SQL runs against the live DB.
- **Test on the beta site first.**
- An organization **credit card must still be on file** even for re-installs/updates (a store requirement, not something the block can bypass).

## Known limitations / honest caveats

- **`web.config` XDT transforms** (uncommon) are applied during the loop, not deferred, and can trigger an earlier recycle. Install that kind of plugin on its own.
- **A locked DLL** can make a single `/bin` copy fail; the block reports those and logs them to the Exception Log. Re-run or apply that one plugin individually.
- **No public store API exists** for headless/scripted installs. This block minimizes login + restart to one each — it can't eliminate them.
- Re-validate the `BinDirectoryBlacklist` after a **major Rock upgrade**.
