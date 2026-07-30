using System;
using System.Collections.Generic;
using System.Text;

namespace Data
{
    public class AgentPromptRecord
    {
        public string Role { get; set; } = "";
        public string Prompt { get; set; } = "";
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
