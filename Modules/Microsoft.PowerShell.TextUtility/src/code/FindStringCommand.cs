// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace Microsoft.PowerShell.TextUtility
{
    [Cmdlet(VerbsCommon.Find, "String")]
    [OutputType(typeof(string))]
    public sealed class FindStringCommand : PSCmdlet
    {
        /// <summary>
        /// Gets or sets the input object to select text.
        /// </summary>
        [Parameter(ValueFromPipeline = true, Mandatory = true)]
        [AllowNull]
        [AllowEmptyString]
        public PSObject InputObject { get; set; }

        /// <summary>
        /// Gets or sets the pattern to select.
        /// </summary>
        [Parameter(Position=0, Mandatory=true)]
        public string Pattern { get; set; }


        /// <summary>
        /// Make the search case sensitive
        /// </summary>
        [Parameter()]
        public SwitchParameter CaseSensitive;

        private Regex _regex;
        private bool _isFormattingObjects = false;
        private List<PSObject> _psobjects = new List<PSObject>();
        private const string EmphasisColor = "\x1b[7;1;32m";
        private const string ResetColor = "\x1b[0m";
        private RegexOptions _regexOptions = RegexOptions.IgnoreCase | RegexOptions.Multiline;
        private readonly string FormatEntryScript = @"
            function FindString($input, $pattern)
            {
                $formatTypes = @(
                    'Microsoft.PowerShell.Commands.Internal.Format.FormatStartData'
                    'Microsoft.PowerShell.Commands.Internal.Format.GroupStartData'
                    'Microsoft.PowerShell.Commands.Internal.Format.GroupEndData'
                    'Microsoft.PowerShell.Commands.Internal.Format.FormatEndData'
                )

                foreach ($entry in $input) {
                    if ($formatTypes -Contains $entry.GetType()) {
                        $entry
                    }
                    else {
                        $entry.formatEntryInfo.formatPropertyFieldList | Where-Object propertyValue -match $pattern |
                            ForEach-Object {
                                $_.propertyValue = $_.propertyValue -replace ""($pattern)"", ""{EmphasisColor}`$1{ResetColor}""
                                $entry
                            }
                    }
                }
            }
        ".Replace("{EmphasisColor}",EmphasisColor).Replace("{ResetColor}",ResetColor);
        protected override void BeginProcessing()
        {
            if (CaseSensitive)
            {
                _regexOptions &= ~RegexOptions.IgnoreCase;
            }

            _regex = new Regex(Pattern, _regexOptions);
        }

        protected override void ProcessRecord()
        {
            if (_isFormattingObjects)
            {
                _psobjects.Add(InputObject);
                return;
            }

            if (InputObject.TypeNames.Contains("Microsoft.PowerShell.Commands.Internal.Format.FormatStartData"))
            {
                _psobjects.Add(InputObject);
                _isFormattingObjects = true;
            }
            else
            {
                using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
                {
                    ps.AddCommand("Out-String").AddParameter("InputObject", InputObject);
                    Collection<PSObject> results = ps.Invoke();
                    foreach (var result in results)
                    {
                        foreach (var line in result.ToString().Split(new []{ Environment.NewLine }, StringSplitOptions.None))
                        {
                            var matches = _regex.Matches(line);
                            if (matches.Count > 0)
                            {
                                var rmatches = new List<Match>();
                                foreach (Match match in matches)
                                {
                                    rmatches.Add(match);
                                }

                                rmatches.Reverse();

                                string matchString = line;
                                foreach (Match match in rmatches)
                                {
                                    matchString = matchString.Insert(match.Index + match.Length, ResetColor);
                                    matchString = matchString.Insert(match.Index, EmphasisColor);
                                }

                                WriteObject(matchString);
                            }
                        }
                    }
                }
            }
        }

        protected override void EndProcessing()
        {
            using (var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                ps.AddScript(FormatEntryScript, useLocalScope: false).Invoke();
                ps.Commands.Clear();
                ps.AddCommand("FindString")
                .AddParameter("input", _psobjects)
                .AddParameter("pattern", Pattern);
                Collection<PSObject> results = ps.Invoke();
                foreach (var result in results)
                {
                    WriteObject(result);
                }
            }
        }
    }
}
