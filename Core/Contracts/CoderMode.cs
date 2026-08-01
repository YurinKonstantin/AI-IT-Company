using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Contracts
{
    public enum CoderMode
    {
        /// <summary>Backend и Frontend — отдельные агенты, работают последовательно.</summary>
        Split = 0,
        /// <summary>Единый агент пишет весь код (Backend + Frontend) за один вызов.</summary>
        Unified = 1
    }
}
