﻿namespace QlikConnectorPSExecute
{
    #region Usings
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Linq;
    using System.Text;
    using QlikView.Qvx.QvxLibrary;
    using System.Management.Automation;
    using System.IO;
    using System.Diagnostics;
    using SimpleImpersonation;
    using System.Security;
    using System.Management.Automation.Runspaces;
    #endregion

    public class PSExecuteConnection : QvxConnection
    {
        #region Properties & Variables
        private StringBuilder Errors { get; set; } = new StringBuilder();
        #endregion 

        #region Init
        public override void Init() { }
        #endregion

        #region Methods
        private IEnumerable<QvxDataRow> GetPowerShellResult()
        {
            var result = new List<QvxDataRow>();
            foreach (DataRow dataRow in internalData.Rows)
            {
                var row = new QvxDataRow();
                for (int i = 0; i < dataRow.ItemArray.Length; i++)
                {
                    row[fields[i]] = dataRow[i].ToString();
                }
                
                result.Add(row);
            }

            return result;
        }       

        private DataTable GetData(ScriptCode script, string username = "", string password = "")
        {
            try
            {
                //Ohne Credentials ist signierung erfoderlich
                //Mit Credentials ist Signierung nicht erforderlich
                //Fussnote mit Stern (Only nesseary at sign)

                if (script == null)
                    return new DataTable(); 

                var resultTable = new DataTable();
                using (var powerShell = PowerShell.Create())
                {
                    powerShell.AddScript($"Start-Job -ScriptBlock {{{script.Code}}}");

                    if (username != "" && password != "")
                    {
                        var secPass = new SecureString();
                        Array.ForEach(password.ToArray(), secPass.AppendChar);
                        var psCred = new PSCredential(username, secPass);
                        powerShell.AddParameter("Credential", psCred);
                    }
                    else
                    {
                        if (!IsQlikDesktopApp())
                        {
                            script.CheckSignature();
                        }
                    }

                    script.Parameters.ToList().ForEach(p => powerShell.AddParameter(p.Key, p.Value));

                    powerShell.AddCommand("Wait-Job");
                    powerShell.AddCommand("Receive-Job");

                    var results = powerShell.Invoke();
                    foreach (var psObject in results)
                    {
                        var row = resultTable.NewRow();
                        foreach (var p in psObject.Properties)
                        {
                            if (!resultTable.Columns.Contains(p.Name))
                            {
                                resultTable.Columns.Add(p.Name);
                            }
                            row[p.Name] = p.Value.ToString();
                        }

                        resultTable.Rows.Add(row);
                    }

                    var errors = powerShell.Streams.Error.ReadAll();
                    foreach (var error in errors)
                    {
                        Errors.Append($"\n{error.Exception.Message}");
                    }
                }

                if (Errors.Length > 0)
                    throw new Exception(Errors.ToString());

                return resultTable;
            }
            catch (Exception ex)
            {
                throw new PowerShellException("The PowerShell script can not be executed.", ex);
            }
        }

        public bool IsQlikDesktopApp()
        {
            try
            {
                return Process.GetCurrentProcess().Parent().MainModule.FileName.Contains(@"AppData\Local\Programs\Qlik\Sense\");
            }
            catch
            {
                return false;
            }
        }

        private DataTable internalData;
        private QvxField[] fields;

        public override QvxDataTable ExtractQuery(string query, List<QvxTable> tables)
        {
            var script = ScriptCode.Parse(query);
           
            var username = "";
            var password = "";
            this.MParameters.TryGetValue("userid", out username);
            this.MParameters.TryGetValue("password", out password);
            username = (username ?? "").Trim();
            password = (password ?? "").Trim();

            internalData = GetData(script, username, password);

            var fieldsl = new List<QvxField>();
            foreach (DataColumn column in internalData.Columns)
            {
                fieldsl.Add(new QvxField(column.ColumnName, QvxFieldType.QVX_TEXT, QvxNullRepresentation.QVX_NULL_FLAG_SUPPRESS_DATA, FieldAttrType.ASCII));
            }
            fields = fieldsl.ToArray();

            var table = new QvxTable()
            {
                TableName = script.TableName,
                Fields = fields,
                GetRows = GetPowerShellResult
            };

            var result = new QvxDataTable(table);
            result.Select(fields);

            return result;
        }
        #endregion      
    }

    #region Exception Classes
    public class PowerShellException : Exception
    {
        public PowerShellException(string message, Exception ex) : base(message, ex) { }
    }
    #endregion
}