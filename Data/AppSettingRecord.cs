using System;
using System.Collections.Generic;
using System.Text;

namespace Data
{
    /// <summary>Простой key/value для настроек приложения (URL Ollama, флаги и т.п.).</summary>
    public class AppSettingRecord
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
    }
}
