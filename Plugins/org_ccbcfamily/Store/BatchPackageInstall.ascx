<%@ Control Language="C#" AutoEventWireup="true" CodeFile="BatchPackageInstall.ascx.cs" Inherits="RockWeb.Plugins.org_ccbcfamily.Store.BatchPackageInstall" %>

<asp:UpdatePanel ID="upnlContent" runat="server">
    <ContentTemplate>

        <div class="panel panel-block">
            <div class="panel-heading">
                <h1 class="panel-title"><i class="fa fa-download"></i> Batch Plugin Update</h1>
            </div>
            <div class="panel-body">

                <%-- Error / status messages --%>
                <asp:Literal ID="lMessages" runat="server" />

                <%-- ===================== LIST / SELECTION PANEL ===================== --%>
                <asp:Panel ID="pnlList" runat="server">

                    <asp:Literal ID="lIntro" runat="server" />

                    <asp:Panel ID="pnlNoUpdates" runat="server" Visible="false">
                        <div class="alert alert-info">
                            <strong>You're all caught up.</strong> No purchased packages have a newer
                            version available that is compatible with this version of Rock.
                        </div>
                    </asp:Panel>

                    <asp:Panel ID="pnlGrid" runat="server">

                        <div class="table-responsive">
                            <table class="grid-table table table-condensed table-light">
                                <thead>
                                    <tr>
                                        <th style="width:40px;">
                                            <input type="checkbox" id="cbSelectAll" title="Select all" />
                                        </th>
                                        <th>Package</th>
                                        <th>Vendor</th>
                                        <th>Installed</th>
                                        <th>Available</th>
                                        <th>Requires Rock</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    <asp:Repeater ID="rptPackages" runat="server">
                                        <ItemTemplate>
                                            <tr>
                                                <td>
                                                    <asp:HiddenField ID="hfPackageId" runat="server" Value='<%# Eval("PackageId") %>' />
                                                    <asp:CheckBox ID="cbSelect" runat="server" CssClass="js-package-select" Checked="true" />
                                                </td>
                                                <td><strong><%# Eval("PackageName") %></strong></td>
                                                <td><%# Eval("VendorName") %></td>
                                                <td><span class="label label-default"><%# Eval("InstalledVersionLabel") %></span></td>
                                                <td><span class="label label-success"><%# Eval("AvailableVersionLabel") %></span></td>
                                                <td><small class="text-muted"><%# Eval("RequiredRockVersion") %></small></td>
                                            </tr>
                                        </ItemTemplate>
                                    </asp:Repeater>
                                </tbody>
                            </table>
                        </div>

                        <hr />

                        <div class="alert alert-warning">
                            <strong>Heads up:</strong> Updating writes new plugin DLLs into the site, so
                            Rock will restart <strong>once</strong> when the batch finishes. SQL changes and
                            files are applied for every selected package first; the DLL swap is done last so
                            you get a single restart instead of one per plugin. Run this during a maintenance
                            window. You only enter your Rock Store login <strong>once</strong> below.
                        </div>

                        <div class="row">
                            <div class="col-md-6">
                                <fieldset>
                                    <legend>Rock Store Login</legend>
                                    <div class="form-group">
                                        <label class="control-label" for="<%= txtUsername.ClientID %>">Username</label>
                                        <asp:TextBox ID="txtUsername" runat="server" CssClass="form-control" autocomplete="off" />
                                    </div>
                                    <div class="form-group">
                                        <label class="control-label" for="<%= txtPassword.ClientID %>">Password</label>
                                        <asp:TextBox ID="txtPassword" runat="server" CssClass="form-control" TextMode="Password" autocomplete="off" />
                                    </div>
                                </fieldset>
                            </div>
                        </div>

                        <div class="form-group">
                            <asp:CheckBox ID="cbAgreeToTerms" runat="server" AutoPostBack="true"
                                OnCheckedChanged="cbAgreeToTerms_CheckedChanged"
                                Text=" I understand each plugin's license terms apply and that Rock will restart when finished." />
                        </div>

                        <asp:LinkButton ID="btnInstall" runat="server" CssClass="btn btn-primary" Enabled="false"
                            OnClick="btnInstall_Click" Text="Install Selected Updates"
                            OnClientClick="if (!confirm('Install all selected plugin updates now? Rock will restart when finished.')) return false;" />

                        <asp:LinkButton ID="btnRefresh" runat="server" CssClass="btn btn-link"
                            OnClick="btnRefresh_Click" Text="Refresh List" CausesValidation="false" />
                    </asp:Panel>
                </asp:Panel>

                <%-- ===================== RESULTS PANEL ===================== --%>
                <asp:Panel ID="pnlResults" runat="server" Visible="false">
                    <asp:Literal ID="lRestartNotice" runat="server" />
                    <div class="table-responsive">
                        <table class="grid-table table table-condensed table-light">
                            <thead>
                                <tr>
                                    <th>Package</th>
                                    <th>Result</th>
                                    <th>Details</th>
                                </tr>
                            </thead>
                            <tbody>
                                <asp:Literal ID="lResultRows" runat="server" />
                            </tbody>
                        </table>
                    </div>
                </asp:Panel>

            </div>
        </div>

        <script type="text/javascript">
            (function () {
                // Wire up the header "select all" box. Re-bound on every partial postback.
                function bindSelectAll() {
                    var master = document.getElementById('cbSelectAll');
                    if (!master) { return; }
                    master.onclick = function () {
                        var boxes = document.querySelectorAll('.js-package-select input[type=checkbox]');
                        for (var i = 0; i < boxes.length; i++) { boxes[i].checked = master.checked; }
                    };
                }
                bindSelectAll();
                if (typeof Sys !== 'undefined' && Sys.WebForms) {
                    Sys.WebForms.PageRequestManager.getInstance().add_endRequest(bindSelectAll);
                }
            })();
        </script>

    </ContentTemplate>
</asp:UpdatePanel>
