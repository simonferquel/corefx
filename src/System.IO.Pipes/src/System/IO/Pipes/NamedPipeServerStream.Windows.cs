// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace System.IO.Pipes
{
    /// <summary>
    /// Named pipe server
    /// </summary>
    public sealed partial class NamedPipeServerStream : PipeStream
    {
        /// <summary>
        /// Full named pipe server constructor
        /// </summary>
        /// <param name="pipeName">Pipe name</param>
        /// <param name="direction">Pipe direction: In, Out or InOut (duplex). 
        /// Win32 note: this gets OR'd into dwOpenMode to CreateNamedPipe
        /// </param>
        /// <param name="maxNumberOfServerInstances">Maximum number of server instances. Specify a fixed value between 
        /// 1 and 254 (Windows)/greater than 1 (Unix), or use NamedPipeServerStream.MaxAllowedServerInstances to use the 
        /// maximum amount allowed by system resources.</param>
        /// <param name="transmissionMode">Byte mode or message mode.
        /// Win32 note: this gets used for dwPipeMode. CreateNamedPipe allows you to specify PIPE_TYPE_BYTE/MESSAGE
        /// and PIPE_READMODE_BYTE/MESSAGE independently, but this sets type and readmode to match.
        /// </param>
        /// <param name="options">PipeOption enum: None, Asynchronous, or Write-through
        /// Win32 note: this gets passed in with dwOpenMode to CreateNamedPipe. Asynchronous corresponds to 
        /// FILE_FLAG_OVERLAPPED option. PipeOptions enum doesn't expose FIRST_PIPE_INSTANCE option because
        /// this sets that automatically based on the number of instances specified.
        /// </param>
        /// <param name="inBufferSize">Incoming buffer size, 0 or higher.
        /// Note: this size is always advisory; OS uses a suggestion.
        /// </param>
        /// <param name="outBufferSize">Outgoing buffer size, 0 or higher (see above)</param>
        /// <param name="inheritability">Whether handle is inheritable</param>
        /// <param name="pipeSecurity"></param>
        internal NamedPipeServerStream(string pipeName, PipeDirection direction, int maxNumberOfServerInstances,
                PipeTransmissionMode transmissionMode, PipeOptions options, int inBufferSize, int outBufferSize,
                HandleInheritability inheritability, PipeSecurity pipeSecurity)
            : base(direction, transmissionMode, outBufferSize)
        {
            if (pipeName == null)
            {
                throw new ArgumentNullException(nameof(pipeName));
            }
            if (pipeName.Length == 0)
            {
                throw new ArgumentException(SR.Argument_NeedNonemptyPipeName);
            }
            if ((options & ~(PipeOptions.WriteThrough | PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), SR.ArgumentOutOfRange_OptionsInvalid);
            }
            if (inBufferSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(inBufferSize), SR.ArgumentOutOfRange_NeedNonNegNum);
            }
            if ((maxNumberOfServerInstances < 1 || maxNumberOfServerInstances > 254) && (maxNumberOfServerInstances != MaxAllowedServerInstances))
            {
                // win32 allows fixed values of 1-254 or 255 to mean max allowed by system. We expose 255 as -1 (unlimited)
                // through the MaxAllowedServerInstances constant. This is consistent e.g. with -1 as infinite timeout, etc.
                // We do this check for consistency on Unix, even though maxNumberOfServerInstances is otherwise ignored.
                throw new ArgumentOutOfRangeException(nameof(maxNumberOfServerInstances), SR.ArgumentOutOfRange_MaxNumServerInstances);
            }

            // inheritability will always be None since this private constructor is only called from other constructors from which
            // inheritability is always set to None. Desktop has a public constructor to allow setting it to something else, but Core
            // doesn't.
            if (inheritability < HandleInheritability.None || inheritability > HandleInheritability.Inheritable)
            {
                throw new ArgumentOutOfRangeException(nameof(inheritability), SR.ArgumentOutOfRange_HandleInheritabilityNoneOrInheritable);
            }

            if ((options & PipeOptions.CurrentUserOnly) != 0)
            {
                IsCurrentUserOnly = true;
            }

            Create(pipeName, direction, maxNumberOfServerInstances, transmissionMode,
            options, inBufferSize, outBufferSize, pipeSecurity, inheritability, 0);
        }
        
        private void Create(string pipeName, PipeDirection direction, int maxNumberOfServerInstances,
                PipeTransmissionMode transmissionMode, PipeOptions options, int inBufferSize, int outBufferSize,
                HandleInheritability inheritability)
        {
            Create(pipeName, direction, maxNumberOfServerInstances, transmissionMode, options, inBufferSize,
                outBufferSize, null, inheritability, 0);
        }

        // This overload is used in Mono to implement public constructors.
        private void Create(string pipeName, PipeDirection direction, int maxNumberOfServerInstances,
                PipeTransmissionMode transmissionMode, PipeOptions options, int inBufferSize, int outBufferSize,
                PipeSecurity pipeSecurity, HandleInheritability inheritability, PipeAccessRights additionalAccessRights)
        {
            Debug.Assert(pipeName != null && pipeName.Length != 0, "fullPipeName is null or empty");
            Debug.Assert(direction >= PipeDirection.In && direction <= PipeDirection.InOut, "invalid pipe direction");
            Debug.Assert(inBufferSize >= 0, "inBufferSize is negative");
            Debug.Assert(outBufferSize >= 0, "outBufferSize is negative");
            Debug.Assert((maxNumberOfServerInstances >= 1 && maxNumberOfServerInstances <= 254) || (maxNumberOfServerInstances == MaxAllowedServerInstances), "maxNumberOfServerInstances is invalid");
            Debug.Assert(transmissionMode >= PipeTransmissionMode.Byte && transmissionMode <= PipeTransmissionMode.Message, "transmissionMode is out of range");

            string fullPipeName = Path.GetFullPath(@"\\.\pipe\" + pipeName);

            // Make sure the pipe name isn't one of our reserved names for anonymous pipes.
            if (string.Equals(fullPipeName, @"\\.\pipe\anonymous", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentOutOfRangeException(nameof(pipeName), SR.ArgumentOutOfRange_AnonymousReserved);
            }

            if (IsCurrentUserOnly)
            {
                Debug.Assert(pipeSecurity == null);

                using (WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent())
                {
                    SecurityIdentifier identifier = currentIdentity.Owner;

                    // Grant full control to the owner so multiple servers can be opened.
                    // Full control is the default per MSDN docs for CreateNamedPipe.
                    PipeAccessRule rule = new PipeAccessRule(identifier, PipeAccessRights.FullControl, AccessControlType.Allow);
                    pipeSecurity = new PipeSecurity();

                    pipeSecurity.AddAccessRule(rule);
                    pipeSecurity.SetOwner(identifier);
                }

                // PipeOptions.CurrentUserOnly is special since it doesn't match directly to a corresponding Win32 valid flag.
                // Remove it, while keeping others untouched since historically this has been used as a way to pass flags to CreateNamedPipe
                // that were not defined in the enumeration.
                options &= ~PipeOptions.CurrentUserOnly;
            }

            int openMode = ((int)direction) |
                           (maxNumberOfServerInstances == 1 ? Interop.Kernel32.FileOperations.FILE_FLAG_FIRST_PIPE_INSTANCE : 0) |
                           (int)options |
                           (int)additionalAccessRights;

            // We automatically set the ReadMode to match the TransmissionMode.
            int pipeModes = (int)transmissionMode << 2 | (int)transmissionMode << 1;

            // Convert -1 to 255 to match win32 (we asserted that it is between -1 and 254).
            if (maxNumberOfServerInstances == MaxAllowedServerInstances)
            {
                maxNumberOfServerInstances = 255;
            }

            var pinningHandle = new GCHandle();
            try
            {
                Interop.Kernel32.SECURITY_ATTRIBUTES secAttrs = PipeStream.GetSecAttrs(inheritability, pipeSecurity, ref pinningHandle);
                SafePipeHandle handle = Interop.Kernel32.CreateNamedPipe(fullPipeName, openMode, pipeModes,
                    maxNumberOfServerInstances, outBufferSize, inBufferSize, 0, ref secAttrs);

                if (handle.IsInvalid)
                {
                    throw Win32Marshal.GetExceptionForLastWin32Error();
                }

                InitializeHandle(handle, false, (options & PipeOptions.Asynchronous) != 0);
            }
            finally
            {
                if (pinningHandle.IsAllocated)
                {
                    pinningHandle.Free();
                }
            }
        }

        // This will wait until the client calls Connect().  If we return from this method, we guarantee that
        // the client has returned from its Connect call.   The client may have done so before this method 
        // was called (but not before this server is been created, or, if we were servicing another client, 
        // not before we called Disconnect), in which case, there may be some buffer already in the pipe waiting
        // for us to read.  See NamedPipeClientStream.Connect for more information.
        [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands", Justification = "Security model of pipes: demand at creation but no subsequent demands")]
        public void WaitForConnection()
        {
            CheckConnectOperationsServerWithHandle();

            if (IsAsync)
            {
                WaitForConnectionCoreAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            else
            {
                if (!Interop.Kernel32.ConnectNamedPipe(InternalHandle, IntPtr.Zero))
                {
                    int errorCode = Marshal.GetLastWin32Error();

                    if (errorCode != Interop.Errors.ERROR_PIPE_CONNECTED)
                    {
                        throw Win32Marshal.GetExceptionForWin32Error(errorCode);
                    }

                    // pipe already connected
                    if (errorCode == Interop.Errors.ERROR_PIPE_CONNECTED && State == PipeState.Connected)
                    {
                        throw new InvalidOperationException(SR.InvalidOperation_PipeAlreadyConnected);
                    }
                    // If we reach here then a connection has been established.  This can happen if a client 
                    // connects in the interval between the call to CreateNamedPipe and the call to ConnectNamedPipe. 
                    // In this situation, there is still a good connection between client and server, even though 
                    // ConnectNamedPipe returns zero.
                }
                State = PipeState.Connected;
            }
        }

        public Task WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            if (!IsAsync)
            {
                return Task.Factory.StartNew(s => ((NamedPipeServerStream)s).WaitForConnection(),
                    this, cancellationToken, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }

            return WaitForConnectionCoreAsync(cancellationToken);
        }

        public void Disconnect()
        {
            CheckDisconnectOperations();

            // Disconnect the pipe.
            if (!Interop.Kernel32.DisconnectNamedPipe(InternalHandle))
            {
                throw Win32Marshal.GetExceptionForLastWin32Error();
            }

            State = PipeState.Disconnected;
        }

        // Gets the username of the connected client.  Note that we will not have access to the client's 
        // username until it has written at least once to the pipe (and has set its impersonationLevel 
        // argument appropriately). 
        public unsafe string GetImpersonationUserName()
        {
            CheckWriteOperations();

            const uint UserNameMaxLength = Interop.Kernel32.CREDUI_MAX_USERNAME_LENGTH + 1;
            char* userName = stackalloc char[(int)UserNameMaxLength]; // ~1K

            if (Interop.Kernel32.GetNamedPipeHandleStateW(InternalHandle, null, null, null, null, userName, UserNameMaxLength))
            {
                return new string(userName);
            }

            return HandleGetImpersonationUserNameError(Marshal.GetLastWin32Error(), UserNameMaxLength, userName);
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        // This method calls a delegate while impersonating the client. Note that we will not have
        // access to the client's security token until it has written at least once to the pipe 
        // (and has set its impersonationLevel argument appropriately). 
        public void RunAsClient(PipeStreamImpersonationWorker impersonationWorker)
        {
            CheckWriteOperations();
            ExecuteHelper execHelper = new ExecuteHelper(impersonationWorker, InternalHandle);
            RuntimeHelpers.ExecuteCodeWithGuaranteedCleanup(tryCode, cleanupCode, execHelper);

            // now handle win32 impersonate/revert specific errors by throwing corresponding exceptions
            if (execHelper._impersonateErrorCode != 0)
            {
                throw WinIOError(execHelper._impersonateErrorCode);
            }
            else if (execHelper._revertImpersonateErrorCode != 0)
            {
                throw WinIOError(execHelper._revertImpersonateErrorCode);
            }
        }

        // the following are needed for CER

        private static RuntimeHelpers.TryCode tryCode = new RuntimeHelpers.TryCode(ImpersonateAndTryCode);
        private static RuntimeHelpers.CleanupCode cleanupCode = new RuntimeHelpers.CleanupCode(RevertImpersonationOnBackout);

        private static void ImpersonateAndTryCode(object helper)
        {
            ExecuteHelper execHelper = (ExecuteHelper)helper;

            RuntimeHelpers.PrepareConstrainedRegions();
            try { }
            finally
            {
                if (Interop.Advapi32.ImpersonateNamedPipeClient(execHelper._handle))
                {
                    execHelper._mustRevert = true;
                }
                else
                {
                    execHelper._impersonateErrorCode = Marshal.GetLastWin32Error();
                }

            }

            if (execHelper._mustRevert)
            { // impersonate passed so run user code
                execHelper._userCode();
            }
        }

        private static void RevertImpersonationOnBackout(object helper, bool exceptionThrown)
        {
            ExecuteHelper execHelper = (ExecuteHelper)helper;

            if (execHelper._mustRevert)
            {
                if (!Interop.Advapi32.RevertToSelf())
                {
                    execHelper._revertImpersonateErrorCode = Marshal.GetLastWin32Error();
                }
            }
        }

        internal class ExecuteHelper
        {
            internal PipeStreamImpersonationWorker _userCode;
            internal SafePipeHandle _handle;
            internal bool _mustRevert;
            internal int _impersonateErrorCode;
            internal int _revertImpersonateErrorCode;

            internal ExecuteHelper(PipeStreamImpersonationWorker userCode, SafePipeHandle handle)
            {
                _userCode = userCode;
                _handle = handle;
            }
        }

        // Async version of WaitForConnection.  See the comments above for more info.
        private unsafe Task WaitForConnectionCoreAsync(CancellationToken cancellationToken)
        {
            CheckConnectOperationsServerWithHandle();

            if (!IsAsync)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeNotAsync);
            }

            var completionSource = new ConnectionCompletionSource(this);

            if (!Interop.Kernel32.ConnectNamedPipe(InternalHandle, completionSource.Overlapped))
            {
                int errorCode = Marshal.GetLastWin32Error();

                switch (errorCode)
                {
                    case Interop.Errors.ERROR_IO_PENDING:
                        break;

                    // If we are here then the pipe is already connected, or there was an error
                    // so we should unpin and free the overlapped.
                    case Interop.Errors.ERROR_PIPE_CONNECTED:
                        // IOCompletitionCallback will not be called because we completed synchronously.
                        completionSource.ReleaseResources();
                        if (State == PipeState.Connected)
                        {
                            throw new InvalidOperationException(SR.InvalidOperation_PipeAlreadyConnected);
                        }
                        completionSource.SetCompletedSynchronously();

                        // We return a cached task instead of TaskCompletionSource's Task allowing the GC to collect it.
                        return Task.CompletedTask;

                    default:
                        completionSource.ReleaseResources();
                        throw Win32Marshal.GetExceptionForWin32Error(errorCode);
                }
            }

            // If we are here then connection is pending.
            completionSource.RegisterForCancellation(cancellationToken);

            return completionSource.Task;
        }

        private void CheckConnectOperationsServerWithHandle()
        {
            if (InternalHandle == null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_PipeHandleNotSet);
            }
            CheckConnectOperationsServer();
        }
    }
}
