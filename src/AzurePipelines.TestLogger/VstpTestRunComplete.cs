using System.Collections.Generic;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;

namespace AzurePipelines.TestLogger
{
    internal class VstpTestRunComplete
    {
        public VstpTestRunComplete(ICollection<AttachmentSet> attachmentSets)
        {
            Attachments = attachmentSets;
        }

        public ICollection<AttachmentSet> Attachments { get; set; }
    }
}