// <copyright>
// Copyright by the Spark Development Network
//
// Licensed under the Rock Community License (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.rockrms.com/license
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>
//
// =====================================================================================
//  Batch Plugin Update  (org_ccbcfamily / Store)
// -------------------------------------------------------------------------------------
//  Drop-in Rock Shop helper that installs MANY purchased-package updates in a single
//  pass with ONE authentication and ONE application restart.
//
//  How it solves the two pain points of the stock one-at-a-time installer:
//
//    1. ONE LOGIN  - The stock "Package Install" block calls StoreService.Purchase()
//                    with your Rock Store credentials for each package, on a separate
//                    page each time, so you re-type the password for every plugin.
//                    Here you enter it once and we re-use it for every Purchase() call
//                    in the batch.
//
//    2. ONE RESTART - Writing a DLL into ~/bin forces ASP.NET to recycle the app domain.
//                     The stock flow extracts each plugin (including its /bin DLLs) on
//                     its own request, so every plugin = its own restart. Here we run
//                     each package's run.sql and extract all NON-/bin content during the
//                     loop, but HOLD every /bin write in a staging folder and flush them
//                     all in one tight loop at the very end of the single postback. The
//                     app domain therefore recycles exactly once, after this request
//                     completes (ASP.NET lets the in-flight request finish before
//                     unloading, so all DLLs land safely).
//
//  This intentionally reuses the exact install primitives from Rock's own
//  RockWeb/Blocks/Store/PackageInstall.ascx.cs (XDT transforms, run.sql, deletefile.lst,
//  core-DLL blacklist, InstalledPackageService bookkeeping) so behavior matches core.
//
//  NOTE: A plugin whose XDT transform targets web.config (uncommon) can still trigger an
//  earlier recycle, because web.config changes are watched too. Those are rare; the vast
//  majority of plugin updates only touch /bin DLLs + SQL, which this block fully batches.
// =====================================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.UI;
using System.Web.UI.WebControls;

using Microsoft.Web.XmlTransform;

using Rock;
using Rock.Attribute;
using Rock.Data;
using Rock.Store;
using Rock.Utility;
using Rock.VersionInfo;
using Rock.Web.Cache;
using Rock.Web.UI;

namespace RockWeb.Plugins.org_ccbcfamily.Store
{
    [DisplayName( "Batch Plugin Update" )]
    [Category( "CCBC > Store" )]
    [Description( "Installs every selected Rock Shop package update in one pass with a single store login and a single application restart." )]
    [Rock.SystemGuid.BlockTypeGuid( "B8E1A9C4-3F27-4D6E-9A11-7C2F5E0D4A88" )]
    public partial class BatchPackageInstall : RockBlock
    {
        #region Constants

        private const string XDT_EXTENSION = ".rock.xdt";

        /// <summary>
        /// DLLs that are Rock core and must never be overwritten/deleted by a plugin.
        /// Kept in sync with RockWeb/Blocks/Store/PackageInstall.ascx.cs.
        /// </summary>
        private static readonly string[] BinDirectoryBlacklist = new[]
        {
            "Azure.AI.OpenAI.dll",
            "Azure.Core.dll",
            "Google.Protobuf.dll",
            "Microsoft.ML.Tokenizers.Data.O200kBase.dll",
            "Microsoft.ML.Tokenizers.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "Microsoft.Bcl.HashCode.dll",
            "Microsoft.Bcl.Memory.dll",
            "Microsoft.Bcl.Numerics.dll",
            "Microsoft.Extensions.AI.dll",
            "Microsoft.Extensions.AI.Abstractions.dll",
            "Microsoft.Extensions.AI.OpenAI.dll",
            "Microsoft.Extensions.Caching.Memory.dll",
            "Microsoft.Extensions.Caching.Abstractions.dll",
            "Microsoft.Extensions.DependencyInjection.dll",
            "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll",
            "Microsoft.Extensions.Options.dll",
            "Microsoft.Extensions.Primitives.dll",
            "Microsoft.Extensions.VectorData.Abstractions.dll",
            "Microsoft.Recognizers.Definitions.dll",
            "Microsoft.Recognizers.Text.dll",
            "Microsoft.Recognizers.Text.DateTime.dll",
            "Microsoft.Recognizers.Text.DataTypes.TimexExpression.dll",
            "Microsoft.Recognizers.Text.Number.dll",
            "Microsoft.Recognizers.Text.NumberWithUnit.dll",
            "Microsoft.SemanticKernel.dll",
            "Microsoft.SemanticKernel.Abstractions.dll",
            "Microsoft.SemanticKernel.Connectors.AzureOpenAI.dll",
            "Microsoft.SemanticKernel.Connectors.OpenAI.dll",
            "Microsoft.SemanticKernel.Core.dll",
            "OpenAI.dll",
            "System.Buffers.dll",
            "System.ClientModel.dll",
            "System.Collections.Immutable.dll",
            "System.Diagnostics.DiagnosticSource.dll",
            "System.IO.Pipelines.dll",
            "System.Memory.dll",
            "System.Memory.Data.dll",
            "System.Numerics.Tensors.dll",
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Text.Json.dll",
            "System.Text.Encodings.Web.dll",
            "System.Threading.Channels.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.ValueTuple.dll",
        };

        #endregion

        #region Nested types

        /// <summary>A pending change to the ~/bin folder, held until the final flush.</summary>
        private class DeferredBinOp
        {
            public bool IsDelete;
            public string StagedPath;   // source file in the staging area (for copies)
            public string DestPath;     // absolute destination under ~/bin
            public string PackageName;  // package that queued it, for error reporting
        }

        /// <summary>One row in the post-run results table.</summary>
        private class InstallResult
        {
            public string PackageName;
            public bool Success;
            public string Detail;
        }

        #endregion

        #region Base control methods

        protected override void OnLoad( EventArgs e )
        {
            if ( IsPostBack )
            {
                // A large batch can take a while (downloads + SQL). Give it room.
                Server.ScriptTimeout = 3600;
                var sm = ScriptManager.GetCurrent( Page );
                if ( sm != null )
                {
                    sm.AsyncPostBackTimeout = 3600;
                }
            }
            else
            {
                BindAvailableUpdates();
            }

            base.OnLoad( e );
        }

        #endregion

        #region Events

        protected void cbAgreeToTerms_CheckedChanged( object sender, EventArgs e )
        {
            btnInstall.Enabled = cbAgreeToTerms.Checked;
        }

        protected void btnRefresh_Click( object sender, EventArgs e )
        {
            lMessages.Text = string.Empty;
            BindAvailableUpdates();
        }

        protected void btnInstall_Click( object sender, EventArgs e )
        {
            lMessages.Text = string.Empty;

            if ( !StoreService.OrganizationIsConfigured() )
            {
                ShowError( "The Rock Store is not configured for this organization yet (no Store Key). Configure it on the Rock Shop &gt; Account page first." );
                return;
            }

            var username = txtUsername.Text.Trim();
            var password = txtPassword.Text;
            if ( username.IsNullOrWhiteSpace() || password.IsNullOrWhiteSpace() )
            {
                ShowError( "Enter your Rock Store username and password." );
                return;
            }

            // Which packages did the user check?
            var selectedPackageIds = GetSelectedPackageIds();
            if ( !selectedPackageIds.Any() )
            {
                ShowError( "Select at least one package to update." );
                return;
            }

            RunBatch( username, password, selectedPackageIds );
        }

        #endregion

        #region Listing

        /// <summary>
        /// Builds the list of purchased packages that have a newer version available that is
        /// compatible with the currently running version of Rock.
        /// </summary>
        private void BindAvailableUpdates()
        {
            pnlResults.Visible = false;
            pnlList.Visible = true;

            if ( !StoreService.OrganizationIsConfigured() )
            {
                pnlGrid.Visible = false;
                pnlNoUpdates.Visible = false;
                ShowError( "The Rock Store is not configured for this organization yet (no Store Key). Configure it on the Rock Shop &gt; Account page first." );
                return;
            }

            string errorResponse;
            var packageService = new PackageService();
            var purchased = packageService.GetPurchasedPackages( out errorResponse );

            if ( errorResponse.IsNotNullOrWhiteSpace() )
            {
                pnlGrid.Visible = false;
                ShowError( "Could not reach the Rock Store: " + errorResponse );
                return;
            }

            var rockVersion = RockSemanticVersion.Parse( VersionInfo.GetRockSemanticVersionNumber() );
            var rows = new List<object>();

            foreach ( var package in purchased )
            {
                // The version we'd install: newest version compatible with this Rock build.
                var latest = package.Versions
                    .Where( v => v.RequiredRockSemanticVersion <= rockVersion )
                    .OrderByDescending( v => v.Id )
                    .FirstOrDefault();

                if ( latest == null )
                {
                    continue; // nothing compatible with this Rock version
                }

                var installed = InstalledPackageService.InstalledPackageVersion( package.Id );

                // Only show packages that are installed AND have a newer compatible version.
                if ( installed == null || latest.Id <= installed.VersionId )
                {
                    continue;
                }

                rows.Add( new
                {
                    PackageId = package.Id,
                    PackageName = package.Name,
                    VendorName = package.Vendor != null ? package.Vendor.Name : string.Empty,
                    InstalledVersionLabel = installed.VersionLabel,
                    AvailableVersionLabel = latest.VersionLabel,
                    RequiredRockVersion = latest.RequiredRockVersion
                } );
            }

            lIntro.Text = "<p>These purchased packages have a newer version available for this version of Rock. "
                + "Pick the ones to update, log in once, and they'll all install in a single restart.</p>";

            if ( rows.Any() )
            {
                pnlGrid.Visible = true;
                pnlNoUpdates.Visible = false;
                rptPackages.DataSource = rows;
                rptPackages.DataBind();
            }
            else
            {
                pnlGrid.Visible = false;
                pnlNoUpdates.Visible = true;
            }
        }

        private List<int> GetSelectedPackageIds()
        {
            var ids = new List<int>();
            foreach ( RepeaterItem item in rptPackages.Items )
            {
                var cb = item.FindControl( "cbSelect" ) as CheckBox;
                var hf = item.FindControl( "hfPackageId" ) as HiddenField;
                if ( cb != null && hf != null && cb.Checked )
                {
                    ids.Add( hf.Value.AsInteger() );
                }
            }
            return ids;
        }

        #endregion

        #region Batch install

        private void RunBatch( string username, string password, List<int> packageIds )
        {
            var storeService = new StoreService();
            var rockVersion = RockSemanticVersion.Parse( VersionInfo.GetRockSemanticVersionNumber() );

            string appRoot = Server.MapPath( "~/" );
            string rockShopWorkingDir = appRoot + "App_Data/RockShop";
            string stagingRoot = appRoot + "App_Data/RockShop/_BatchStaging";

            // Start from a clean staging area each run.
            TryDeleteDirectory( stagingRoot );
            EnsureDirectoryExists( rockShopWorkingDir );
            EnsureDirectoryExists( stagingRoot );

            var deferredBinOps = new List<DeferredBinOp>();
            var results = new List<InstallResult>();

            foreach ( var packageId in packageIds )
            {
                var result = new InstallResult { Success = false };
                results.Add( result );

                try
                {
                    string purchaseError;
                    var purchaseResponse = storeService.Purchase( username, password, packageId, out purchaseError );

                    if ( purchaseResponse == null )
                    {
                        result.PackageName = "Package " + packageId;
                        result.Detail = "Authentication/store error: " + ( purchaseError.IsNotNullOrWhiteSpace() ? purchaseError : "unknown." );
                        continue;
                    }

                    result.PackageName = purchaseResponse.PackageName.IsNotNullOrWhiteSpace()
                        ? purchaseResponse.PackageName
                        : "Package " + packageId;

                    if ( purchaseResponse.PurchaseResult != PurchaseResult.Success )
                    {
                        result.Detail = DescribePurchaseFailure( purchaseResponse );

                        // A bad login will fail every remaining package too - stop early and tell the user.
                        if ( purchaseResponse.PurchaseResult == PurchaseResult.AuthenticationFailed )
                        {
                            FinishWithResults( results, deferredBinOps, stagingRoot,
                                stoppedEarly: "Store authentication failed, so the batch was stopped. Check your Rock Store username/password and try again." );
                            return;
                        }
                        continue;
                    }

                    if ( purchaseResponse.PackageInstallSteps == null )
                    {
                        result.Detail = "Store returned no install steps for this package.";
                        continue;
                    }

                    InstallPackage( purchaseResponse, rockVersion, appRoot, rockShopWorkingDir, stagingRoot, deferredBinOps, result );
                }
                catch ( Exception ex )
                {
                    result.Detail = "Unexpected error: " + ex.Message;
                    LogError( ex );
                }
            }

            FinishWithResults( results, deferredBinOps, stagingRoot, stoppedEarly: null );
        }

        /// <summary>
        /// Applies every applicable install step for one package: downloads each .plugin,
        /// extracts non-/bin content, runs run.sql and deletefile.lst, and stages /bin writes
        /// for the deferred flush. Records the install on success.
        /// </summary>
        private void InstallPackage( PurchaseResponse purchaseResponse, RockSemanticVersion rockVersion,
            string appRoot, string rockShopWorkingDir, string stagingRoot,
            List<DeferredBinOp> deferredBinOps, InstallResult result )
        {
            int currentlyInstalledVersion = GetCurrentlyInstalledPackageVersion( purchaseResponse.PackageId );

            var steps = purchaseResponse.PackageInstallSteps
                .Where( s => s.RequiredRockSemanticVersion <= rockVersion )
                .Where( s => s.VersionId > currentlyInstalledVersion )
                .OrderBy( s => s.VersionId )
                .ToList();

            if ( !steps.Any() )
            {
                result.Success = true;
                result.Detail = "Already up to date.";
                return;
            }

            string packageDirectory = string.Format( "{0}/{1} - {2}", rockShopWorkingDir, purchaseResponse.PackageId, purchaseResponse.PackageName );
            EnsureDirectoryExists( packageDirectory );

            string lastInstructions = string.Empty;
            int appliedVersionCount = 0;

            foreach ( var step in steps )
            {
                string destinationFile = string.Format( "{0}/{1} - {2}.plugin", packageDirectory, step.VersionId, step.VersionLabel );

                // -- download --
                try
                {
                    using ( var wc = new WebClient() )
                    {
                        wc.DownloadFile( step.InstallPackageUrl, destinationFile );
                    }
                }
                catch ( Exception ex )
                {
                    CleanUpFile( destinationFile );
                    throw new Exception( string.Format( "download of version {0} failed: {1}", step.VersionLabel, ex.Message ) );
                }

                // -- extract / sql / deletes (bin writes are staged, not applied) --
                bool wasActionTaken = false;
                using ( ZipArchive packageZip = ZipFile.OpenRead( destinationFile ) )
                {
                    foreach ( ZipArchiveEntry entry in packageZip.Entries )
                    {
                        wasActionTaken = ProcessZipEntry( entry, appRoot, stagingRoot, purchaseResponse.PackageId, result.PackageName, deferredBinOps ) || wasActionTaken;
                    }

                    wasActionTaken = ProcessRunSql( packageZip ) || wasActionTaken;
                    wasActionTaken = ProcessDeleteFileList( packageZip, appRoot, result.PackageName, deferredBinOps ) || wasActionTaken;
                }

                if ( !wasActionTaken )
                {
                    throw new Exception( string.Format( "version {0} contained no installable actions (possibly an improperly packaged plugin).", step.VersionLabel ) );
                }

                // Record this version as installed (App_Data write - does not trigger a recycle).
                InstalledPackageService.SaveInstall(
                    purchaseResponse.PackageId, purchaseResponse.PackageName,
                    step.VersionId, step.VersionLabel,
                    purchaseResponse.VendorId, purchaseResponse.VendorName, purchaseResponse.InstalledBy );

                lastInstructions = step.PostInstallInstructions;
                appliedVersionCount++;
            }

            result.Success = true;
            result.Detail = string.Format( "Updated through {0} ({1} version step{2} applied).{3}",
                steps.Last().VersionLabel,
                appliedVersionCount,
                appliedVersionCount == 1 ? string.Empty : "s",
                lastInstructions.IsNotNullOrWhiteSpace() ? " " + lastInstructions : string.Empty );
        }

        /// <summary>
        /// Runs after every package: clears the cache, flushes all staged /bin writes in one
        /// tight loop (the only thing that triggers the app-domain recycle), then renders results.
        /// </summary>
        private void FinishWithResults( List<InstallResult> results, List<DeferredBinOp> deferredBinOps, string stagingRoot, string stoppedEarly )
        {
            var binFlushFailures = FlushDeferredBinOps( deferredBinOps );

            // Clear caches so Rock re-reads everything when it comes back up.
            RockCache.ClearAllCachedItems();

            // Tidy the staging folder (App_Data - safe, no recycle).
            TryDeleteDirectory( stagingRoot );

            RenderResults( results, deferredBinOps.Count, binFlushFailures, stoppedEarly );
        }

        /// <summary>
        /// Queues a /bin change, replacing any earlier change to the same file. Packages can
        /// ship the same DLL (e.g. BouncyCastle.Crypto.dll in both Church Online Webhooks and
        /// Acme Certificate) and multi-step updates repeat it per version; copying it over
        /// itself back-to-back can hit a lock from the previous write. Last one queued wins,
        /// which matches the order the individual installs would have applied them.
        /// </summary>
        private static void QueueBinOp( List<DeferredBinOp> deferredBinOps, DeferredBinOp op )
        {
            deferredBinOps.RemoveAll( o => string.Equals( o.DestPath, op.DestPath, StringComparison.OrdinalIgnoreCase ) );
            deferredBinOps.Add( op );
        }

        /// <summary>
        /// Copies/deletes every staged /bin item. This is deliberately the LAST thing the
        /// request does, so the resulting app-domain recycle happens once, after we return.
        /// Returns a description of each change that could not be applied.
        /// </summary>
        private List<string> FlushDeferredBinOps( List<DeferredBinOp> ops )
        {
            var failures = new List<string>();
            foreach ( var op in ops )
            {
                try
                {
                    if ( op.IsDelete )
                    {
                        if ( File.Exists( op.DestPath ) )
                        {
                            File.Delete( op.DestPath );
                        }
                    }
                    else
                    {
                        var destDir = Path.GetDirectoryName( op.DestPath );
                        EnsureDirectoryExists( destDir );
                        File.Copy( op.StagedPath, op.DestPath, true );
                        File.SetLastWriteTime( op.DestPath, DateTime.Now );
                    }
                }
                catch ( Exception ex )
                {
                    var description = string.Format( "{0} ({1})", Path.GetFileName( op.DestPath ), op.PackageName );
                    failures.Add( description );
                    LogError( new Exception(
                        string.Format( "Batch Plugin Update could not {0} '{1}' for {2}: {3}",
                            op.IsDelete ? "delete" : "copy", op.DestPath, op.PackageName, ex.Message ),
                        ex ) );
                }
            }
            return failures;
        }

        #endregion

        #region Zip processing (mirrors core PackageInstall, with deferred /bin handling)

        /// <summary>
        /// Processes one content entry. Non-/bin files are written immediately; XDT transforms
        /// are applied immediately; /bin DLLs are extracted to staging and queued for the flush.
        /// </summary>
        private bool ProcessZipEntry( ZipArchiveEntry entry, string appRoot, string stagingRoot, int packageId, string packageName, List<DeferredBinOp> deferredBinOps )
        {
            if ( entry.Length == 0 )
            {
                return false; // directory entry
            }

            var fullName = entry.FullName.Replace( "\\", "/" );
            if ( !fullName.StartsWith( "content/", StringComparison.OrdinalIgnoreCase ) )
            {
                return false;
            }

            string relativePath = fullName.ReplaceFirstOccurrence( "content/", string.Empty );

            // ----- XDT transform -----
            if ( fullName.EndsWith( XDT_EXTENSION, StringComparison.OrdinalIgnoreCase ) )
            {
                string transformTargetFile = appRoot + relativePath.Substring( 0, relativePath.LastIndexOf( XDT_EXTENSION ) );

                using ( var document = new XmlTransformableDocument() )
                {
                    document.PreserveWhitespace = true;
                    document.Load( transformTargetFile );

                    using ( var transform = new XmlTransformation( entry.Open(), null ) )
                    {
                        if ( transform.Apply( document ) )
                        {
                            document.Save( transformTargetFile );
                            return true;
                        }
                    }
                }
                return false;
            }

            // ----- /bin DLL: stage now, copy at the end -----
            if ( IsBinPath( relativePath ) )
            {
                if ( IsBlacklisted( relativePath ) )
                {
                    return false; // never overwrite a core DLL
                }

                string stagedPath = Path.Combine( stagingRoot, packageId.ToString(), relativePath.Replace( "/", Path.DirectorySeparatorChar.ToString() ) );
                EnsureDirectoryExists( Path.GetDirectoryName( stagedPath ) );
                entry.ExtractToFile( stagedPath, true );

                QueueBinOp( deferredBinOps, new DeferredBinOp
                {
                    IsDelete = false,
                    StagedPath = stagedPath,
                    DestPath = Path.Combine( appRoot, relativePath.Replace( "/", Path.DirectorySeparatorChar.ToString() ) ),
                    PackageName = packageName
                } );

                return true;
            }

            // ----- ordinary content file: write immediately (does not trigger recycle) -----
            string fullpath = Path.Combine( appRoot, relativePath );
            string directory = Path.GetDirectoryName( fullpath );
            EnsureDirectoryExists( directory );
            entry.ExtractToFile( fullpath, true );
            File.SetLastWriteTime( fullpath, DateTime.Now );

            return true;
        }

        private bool ProcessRunSql( ZipArchive packageZip )
        {
            var sqlInstallEntry = packageZip.Entries
                .FirstOrDefault( e => e.FullName == "install/run.sql" || e.FullName == "install\\run.sql" );

            if ( sqlInstallEntry == null )
            {
                return false;
            }

            string sqlScript;
            using ( var reader = new StreamReader( sqlInstallEntry.Open() ) )
            {
                sqlScript = reader.ReadToEnd();
            }

            if ( sqlScript.IsNullOrWhiteSpace() )
            {
                return false;
            }

            using ( var context = new RockContext() )
            {
                context.Database.CommandTimeout = 600;
                context.Database.ExecuteSqlCommand( sqlScript );
            }

            return true;
        }

        private bool ProcessDeleteFileList( ZipArchive packageZip, string appRoot, string packageName, List<DeferredBinOp> deferredBinOps )
        {
            var deleteListEntry = packageZip.Entries
                .FirstOrDefault( e => e.FullName == "install/deletefile.lst" || e.FullName == "install\\deletefile.lst" );

            if ( deleteListEntry == null )
            {
                return false;
            }

            string deleteList;
            using ( var reader = new StreamReader( deleteListEntry.Open() ) )
            {
                deleteList = reader.ReadToEnd();
            }

            bool wasActionTaken = false;
            var itemsToDelete = deleteList.Split( new[] { Environment.NewLine, "\n" }, StringSplitOptions.None );

            foreach ( var rawItem in itemsToDelete )
            {
                var deleteItem = rawItem.Trim();
                if ( deleteItem.IsNullOrWhiteSpace() )
                {
                    continue;
                }

                var normalized = deleteItem.Replace( "\\", "/" );
                if ( IsBlacklisted( normalized ) )
                {
                    continue; // never delete a core DLL
                }

                wasActionTaken = true;

                // Defer deletes that target /bin so they happen in the single final flush.
                if ( IsBinPath( normalized ) )
                {
                    QueueBinOp( deferredBinOps, new DeferredBinOp
                    {
                        IsDelete = true,
                        DestPath = Path.Combine( appRoot, normalized.Replace( "/", Path.DirectorySeparatorChar.ToString() ) ),
                        PackageName = packageName
                    } );
                    continue;
                }

                string deleteItemFullPath = appRoot + deleteItem;
                if ( Directory.Exists( deleteItemFullPath ) )
                {
                    Directory.Delete( deleteItemFullPath, true );
                }
                if ( File.Exists( deleteItemFullPath ) )
                {
                    File.Delete( deleteItemFullPath );
                }
            }

            return wasActionTaken;
        }

        #endregion

        #region Helpers

        private int GetCurrentlyInstalledPackageVersion( int packageId )
        {
            var installed = InstalledPackageService.InstalledPackageVersion( packageId );
            return installed != null ? installed.VersionId : -1;
        }

        private static bool IsBinPath( string relativePath )
        {
            return relativePath.StartsWith( "bin/", StringComparison.OrdinalIgnoreCase );
        }

        private static bool IsBlacklisted( string relativePath )
        {
            if ( relativePath.StartsWith( "bin/", StringComparison.OrdinalIgnoreCase ) )
            {
                var filename = Path.GetFileName( relativePath );
                return BinDirectoryBlacklist.Contains( filename, StringComparer.OrdinalIgnoreCase );
            }
            return false;
        }

        private static void EnsureDirectoryExists( string directoryPath )
        {
            if ( directoryPath.IsNotNullOrWhiteSpace() && !Directory.Exists( directoryPath ) )
            {
                Directory.CreateDirectory( directoryPath );
            }
        }

        private static void CleanUpFile( string file )
        {
            try
            {
                if ( File.Exists( file ) )
                {
                    File.Delete( file );
                }
            }
            catch { /* best effort */ }
        }

        private static void TryDeleteDirectory( string dir )
        {
            try
            {
                if ( Directory.Exists( dir ) )
                {
                    Directory.Delete( dir, true );
                }
            }
            catch { /* best effort */ }
        }

        private void LogError( Exception ex )
        {
            try
            {
                Rock.Model.ExceptionLogService.LogException( ex, System.Web.HttpContext.Current );
            }
            catch { /* never let logging break the batch */ }
        }

        private static string DescribePurchaseFailure( PurchaseResponse r )
        {
            switch ( r.PurchaseResult )
            {
                case PurchaseResult.AuthenticationFailed:
                    return "Could not authenticate to the Rock Store. " + r.Message;
                case PurchaseResult.NotAuthorized:
                    return "Your account is not authorized to make purchases for this organization. " + r.Message;
                case PurchaseResult.NoCardOnFile:
                    return "No credit card is on file for this organization (required even when re-installing). " + r.Message;
                case PurchaseResult.PaymentFailed:
                    return "Payment failed: " + r.Message;
                case PurchaseResult.Error:
                    return "Store error: " + r.Message;
                default:
                    return r.Message;
            }
        }

        private void ShowError( string message )
        {
            lMessages.Text = string.Format( "<div class='alert alert-warning'>{0}</div>", message );
        }

        private void RenderResults( List<InstallResult> results, int totalBinOps, List<string> binFlushFailures, string stoppedEarly )
        {
            pnlList.Visible = false;
            pnlResults.Visible = true;

            int successes = results.Count( r => r.Success );
            int failures = results.Count - successes;

            var notice = new StringBuilder();

            if ( stoppedEarly.IsNotNullOrWhiteSpace() )
            {
                notice.AppendFormat( "<div class='alert alert-danger'>{0}</div>", stoppedEarly );
            }

            int appliedBinOps = totalBinOps - binFlushFailures.Count;

            if ( successes > 0 )
            {
                notice.AppendFormat(
                    "<div class='alert alert-success'><strong>{0} package{1} updated.</strong> "
                    + "{2} DLL file change{3} were applied. <strong>Rock is restarting now</strong> to load the new "
                    + "plugin assemblies &mdash; give it a minute, then refresh. This is the single restart for the whole batch.</div>",
                    successes,
                    successes == 1 ? string.Empty : "s",
                    appliedBinOps,
                    appliedBinOps == 1 ? string.Empty : "s" );
            }

            if ( binFlushFailures.Any() )
            {
                notice.AppendFormat(
                    "<div class='alert alert-warning'>{0} DLL file change{1} could not be applied (the file may have been locked): "
                    + "<strong>{2}</strong>. Re-run, or apply that plugin individually. Details are in the Exception Log.</div>",
                    binFlushFailures.Count,
                    binFlushFailures.Count == 1 ? string.Empty : "s",
                    binFlushFailures.Select( f => f.EncodeHtml() ).JoinStrings( ", " ) );
            }

            if ( failures > 0 )
            {
                notice.AppendFormat( "<div class='alert alert-warning'>{0} package{1} did not update. See the table below.</div>",
                    failures, failures == 1 ? string.Empty : "s" );
            }

            lRestartNotice.Text = notice.ToString();

            var rows = new StringBuilder();
            foreach ( var r in results )
            {
                string badge = r.Success
                    ? "<span class='label label-success'>Updated</span>"
                    : "<span class='label label-danger'>Failed</span>";

                rows.AppendFormat( "<tr><td><strong>{0}</strong></td><td>{1}</td><td>{2}</td></tr>",
                    System.Web.HttpUtility.HtmlEncode( r.PackageName ),
                    badge,
                    System.Web.HttpUtility.HtmlEncode( r.Detail ) );
            }
            lResultRows.Text = rows.ToString();
        }

        #endregion
    }
}
