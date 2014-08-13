// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Linq;
using System.Collections.Generic;

namespace Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages
{
    public class ReferencesMessage
    {
        public string RootDependency { get; set; }
        public string LongFrameworkName { get; set; }
        public string FriendlyFrameworkName { get; set; }
        public IList<string> ProjectReferences { get; set; }
        public IList<string> FileReferences { get; set; }
        public IDictionary<string, byte[]> RawReferences { get; set; }
        public IDictionary<string, ReferenceDescription> Dependencies { get; set; }

        public override bool Equals(object obj)
        {
            var other = obj as ReferencesMessage;

            return other != null &&
                   RootDependency.Equals(other.RootDependency) &&
                   LongFrameworkName.Equals(other.LongFrameworkName) &&
                   FriendlyFrameworkName.Equals(other.FriendlyFrameworkName) &&
                   Enumerable.SequenceEqual(ProjectReferences, other.ProjectReferences) &&
                   Enumerable.SequenceEqual(FileReferences, other.FileReferences) &&
                   Enumerable.SequenceEqual(Dependencies, other.Dependencies) &&
                   Enumerable.SequenceEqual(RawReferences, other.RawReferences);
        }

        public override int GetHashCode()
        {
            // These objects are currently POCOs and we're overriding equals
            // so that things like Enumerable.SequenceEqual just work.
            return base.GetHashCode();
        }
    }
}