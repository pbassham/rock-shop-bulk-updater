# Request for Review — Batch Plugin Update block

I'd appreciate a second (and third) set of eyes on this before I run it against a production Rock instance. It's a custom Rock Shop block that updates **many** purchased packages in **one** pass — one store login, one application restart — instead of the stock one‑plugin‑at‑a‑time flow that re‑prompts for the store password and recycles the app domain on every single package.

I wrote it specifically for the current wave of "recompile every plugin for v18/v19" updates, where you can be staring down dozens of pending updates at once.

**What I'm asking:** please read the two source files and tell me if you see any reason this would misbehave, corrupt an install, or fail in a way the stock installer wouldn't. I've listed the assumptions I'm least sure about at the bottom.

## Repo layout

```
rock-shop-bulk-updater/
├── README.md
├── Plugins/org_ccbcfamily/Store/
│   ├── BatchPackageInstall.ascx        (markup; CodeFile, runtime-compiled)
│   └── BatchPackageInstall.ascx.cs     (all the logic)
```

It's a WebForms block (`Rock.Web.UI.RockBlock`), deliberately, because the install work is server-side file IO and a single long postback is where the restart timing is easiest to control.

## Design goal: do NOT reinvent the install protocol

The whole point is that it does **not** reimplement how Rock talks to the store or how a `.plugin` is unpacked. It calls the same public `Rock.Store` methods the core block calls, and it replicates the file-handling in `RockWeb/Blocks/Store/PackageInstall.ascx.cs` step-for-step. The only behavioral change is *batching* and *when* the `/bin` writes happen.

### Core methods/classes it reuses (unchanged)

| What | Core member used |
|------|------------------|
| Is the store configured? | `Rock.Store.StoreService.OrganizationIsConfigured()` |
| List purchased packages + their versions | `Rock.Store.PackageService.GetPurchasedPackages( out errorResponse )` |
| Authenticate + authorize a package, get install steps | `Rock.Store.StoreService.Purchase( username, password, packageId, out errorResponse )` → `PurchaseResponse` |
| What's installed now | `Rock.Store.InstalledPackageService.InstalledPackageVersion( packageId )` |
| Record an install | `Rock.Store.InstalledPackageService.SaveInstall( packageId, name, versionId, versionLabel, vendorId, vendorName, installedBy )` |
| Clear caches after install | `Rock.Web.Cache.RockCache.ClearAllCachedItems()` |
| Models | `Package`, `PackageVersion`, `PurchaseResponse` / `PurchaseResult`, `PackageInstallStep`, `InstalledPackage` |

### File-handling logic copied from core `PackageInstall.ascx.cs`

These are reproduced with the same semantics (same method names, same order):

- `ProcessZipEntry` — extracts `content/...` entries, runs `*.rock.xdt` XML Document Transforms against the target file.
- `ProcessRunSql` — runs `install/run.sql` via `RockContext.Database.ExecuteSqlCommand`.
- `ProcessDeleteFileList` — honors `install/deletefile.lst`.
- `BinDirectoryBlacklist` — the same list of Rock-core DLLs that must never be overwritten/deleted by a plugin (copied verbatim from core).
- Per-version stepping — only applies `PackageInstallStep`s whose `RequiredRockSemanticVersion <= ` the running Rock version and whose `VersionId >` the currently installed version, in `VersionId` order (same filter core uses in `ProcessInstall`).

If you diff the per-package install path against the core block, it should look almost identical.

## Mechanics — how a run actually works

**1. Build the list.** On load it calls `GetPurchasedPackages()`, and for each package finds the newest `PackageVersion` whose `RequiredRockSemanticVersion <= ` the current Rock semantic version. If that version's `Id` is greater than the `InstalledPackageService` record's `VersionId`, the package is shown as having an update. (So it only offers updates that are both newer and compatible with the running build.)

**2. One login for the whole batch.** You enter your Rock Store username/password once. The block reuses those same credentials for each `StoreService.Purchase(...)` call — one call per selected package. There is no caching of the password anywhere; it lives only in the request.

**3. Per package.** For each selected package:
   - `Purchase()` authenticates and returns a `PurchaseResponse` with `PackageInstallSteps`.
   - Steps are filtered/ordered exactly as core does.
   - Each step's `.plugin` is downloaded from `InstallPackageUrl`, opened as a `ZipArchive`, and:
     - **non-`/bin`** `content/` files are extracted immediately,
     - `*.rock.xdt` transforms are applied immediately,
     - `install/run.sql` is executed immediately,
     - `install/deletefile.lst` is processed,
     - and then `InstalledPackageService.SaveInstall(...)` records the version.

**4. The one important difference — deferred `/bin` writes.** Writing a DLL into `~/bin` is what forces ASP.NET to recycle the app domain. So instead of dropping each package's DLLs into `~/bin` as it goes (which is what makes the stock flow restart once *per* plugin), this block:
   - extracts every `/bin` entry into a staging folder under `~/App_Data/RockShop/_BatchStaging`, and queues a "deferred bin op,"
   - and queues any `/bin` deletions from `deletefile.lst` the same way.

   After **all** packages have run (SQL committed, content/files written, install records saved), it calls `RockCache.ClearAllCachedItems()` and then **flushes every queued `/bin` op in one tight loop** — `File.Copy` from staging into `~/bin` (and the deferred deletes).

   Because the `/bin` writes are the very last thing the request does, and ASP.NET lets the in‑flight request finish before unloading the app domain, the batch should produce **exactly one** recycle, after everything else is already on disk and in the database.

**5. Result.** It renders a per-package pass/fail table with each package's `PostInstallInstructions`, and tells you Rock is restarting once.

## Things I'd specifically like reviewed (my least-certain assumptions)

1. **The recycle-deferral premise.** I'm relying on ASP.NET not unloading the app domain until this request completes, so all staged `/bin` files land before the recycle. Is that reliable across IIS configs? Could `FileChangesMonitor` debounce + `shutdownTimeout` ever fire the unload mid-flush on a very large batch? Is there a hard cap I should chunk under?

2. **`run.sql` before the new DLL is loaded.** Core runs `run.sql` during the same request, before the new assemblies are loaded (the load only happens after the recycle). I do the same. Is there any plugin pattern whose `run.sql` assumes its new DLL is already loaded? (If so it'd presumably be broken under the stock installer too — want to confirm I'm not introducing a new ordering problem.)

3. **Calling `Purchase()` per package for updates.** For an already-purchased package, does calling `Purchase()` again to fetch an update's steps have any billing/side-effect I'm not seeing? My read of the store flow is that it re-authorizes but doesn't re-charge for an owned package — confirmation from anyone who knows the store side would help.

4. **`web.config` XDT transforms.** I apply XDT transforms inline (not deferred). If a plugin's update transforms `web.config`, that change is also watched and could trigger an earlier recycle, breaking the "single restart" promise. I documented this as a known caveat — is inline the right call, or should those be deferred too?

5. **Partial-failure bookkeeping.** `SaveInstall` is written per step, before the final `/bin` flush. If a single DLL copy fails at flush time (locked file), the install record says "installed" but that one DLL didn't update. The block reports flush errors and logs them. Does core have the same exposure, or should I roll back the install record on a flush failure?

6. **`run.sql` text encoding.** Core reads `run.sql` with `Encoding.Default`; I read it with a `StreamReader` (UTF‑8 with BOM detection). Could that bite an ANSI-encoded script with extended characters?

7. **Runtime-compiled `CodeFile` block.** It ships as `.ascx` + `.ascx.cs` (CodeFile), expecting RockWeb to compile it at runtime. Any environments where that won't load and it must be pre-compiled into a plugin DLL?

8. **Blacklist drift.** `BinDirectoryBlacklist` is copied from core as of now. Confirm there isn't a better way to source that list at runtime so it doesn't drift after a major upgrade.

9. **Concurrency.** No guard against two admins running it simultaneously, or running it under load. Worth a lock?

Thanks in advance — happy to make changes based on what you find. The two files are short; the deferred-`/bin` logic is in `RunBatch` / `InstallPackage` / `ProcessZipEntry` / `FlushDeferredBinOps`.
