using System;

namespace GameHelper.Infrastructure.Exceptions
{
    /// <summary>
    /// Base exception for ETW monitoring related errors.
    /// </summary>
    public class EtwMonitorException : Exception
    {
        public EtwMonitorException(string message) : base(message) { }
        public EtwMonitorException(string message, Exception innerException) 
            : base(message, innerException) { }
    }

    /// <summary>
    /// Exception thrown when ETW monitoring requires administrator privileges.
    /// </summary>
    public class InsufficientPrivilegesException : EtwMonitorException
    {
        public InsufficientPrivilegesException() 
            : base("ETW monitoring requires administrator privileges. Please run the application as administrator.") { }
        
        public InsufficientPrivilegesException(string message) : base(message) { }
        public InsufficientPrivilegesException(string message, Exception innerException) 
            : base(message, innerException) { }
    }
}