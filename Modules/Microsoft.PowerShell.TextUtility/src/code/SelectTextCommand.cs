// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Text.RegularExpressions;

namespace Microsoft.PowerShell.TextUtility
{
    [Cmdlet(VerbsCommon.Select, "Text")]
    [OutputType(typeof(string))]
    public sealed class SelectTextCommand : PSCmdlet
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
        private List<PSObject> _psobjects = new List<PSObject>();
        private const string EmphasisColor = "\x1b[7;1;32m";
        private const string ResetColor = "\x1b[0m";
        private RegexOptions _regexOptions = RegexOptions.IgnoreCase;
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
            _psobjects.Add(InputObject);
        }

        protected override void EndProcessing()
        {
            using(var ps = System.Management.Automation.PowerShell.Create(RunspaceMode.CurrentRunspace))
            {
                ps.AddCommand("Out-String").AddParameter("InputObject", _psobjects);
                Collection<PSObject> results = ps.Invoke();
                foreach (PSObject result in results)
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
}
