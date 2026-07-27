using System;
using System.Collections.Generic;
using System.Text;

namespace Core.Contracts
{
    public interface IAiProviderFactory
    {
        IAiProvider Resolve(string source);
    }
}
