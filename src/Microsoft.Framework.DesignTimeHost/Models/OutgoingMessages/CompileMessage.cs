﻿using System;
using System.Collections.Generic;

namespace Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages
{
    public class CompileMessage
    {
        public IDictionary<string, byte[]> EmbeddedReferences { get; set; }

        public byte[] AssemblyBytes { get; set; }
        public byte[] PdbBytes { get; set; }

        public string AssemblyPath { get; set; }
    }
}