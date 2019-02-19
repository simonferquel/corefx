// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Reflection;
using System.Net.Sockets;
using System.Net.Test.Common;

namespace System.Net.Http.Functional.Tests
{
    public abstract class HttpClientHandlerTestBase : RemoteExecutorTestBase
    {
        protected virtual bool UseSocketsHttpHandler => true;
        protected virtual bool UseHttp2LoopbackServer => false;
        protected virtual bool UseStreamDialer => false;

        protected bool IsWinHttpHandler => !UseSocketsHttpHandler && PlatformDetection.IsWindows && !PlatformDetection.IsUap && !PlatformDetection.IsFullFramework;
        protected bool IsCurlHandler => !UseSocketsHttpHandler && !PlatformDetection.IsWindows;
        protected bool IsNetfxHandler => PlatformDetection.IsWindows && PlatformDetection.IsFullFramework;
        protected bool IsUapHandler => PlatformDetection.IsWindows && PlatformDetection.IsUap;

        protected HttpClient CreateHttpClient() => new HttpClient(CreateHttpClientHandler());

        protected HttpClientHandler CreateHttpClientHandler() => CreateHttpClientHandler(UseSocketsHttpHandler, UseHttp2LoopbackServer, UseStreamDialer);

        protected static HttpClient CreateHttpClient(string useSocketsHttpHandlerBoolString) =>
            new HttpClient(CreateHttpClientHandler(useSocketsHttpHandlerBoolString));

        protected static HttpClientHandler CreateHttpClientHandler(string useSocketsHttpHandlerBoolString) =>
            CreateHttpClientHandler(bool.Parse(useSocketsHttpHandlerBoolString));

        protected static HttpClientHandler CreateHttpClientHandler(bool useSocketsHttpHandler, bool useHttp2LoopbackServer = false, bool useStreamDialer = false)
        {
            HttpClientHandler handler;

            if (PlatformDetection.IsUap || PlatformDetection.IsFullFramework || useSocketsHttpHandler)
            {
                handler = new HttpClientHandler();
            }
            else
            {
                // Create platform specific handler.
                ConstructorInfo ctor = typeof(HttpClientHandler).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(bool) }, null);
                Debug.Assert(ctor != null, "Couldn't find test constructor on HttpClientHandler");

                handler = (HttpClientHandler)ctor.Invoke(new object[] { useSocketsHttpHandler });
                Debug.Assert(useSocketsHttpHandler == IsSocketsHttpHandler(handler), "Unexpected handler.");                
            }

            if (useStreamDialer && useSocketsHttpHandler)
            {
                var socketsHandler = (GetUnderlyingSocketsHttpHandler(handler) as Http.SocketsHttpHandler);
                socketsHandler.ConnectCallback =  (HttpRequestMessage request, Threading.CancellationToken cancellationToken) =>
                {                    
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    socket.Connect(request.RequestUri.Host, request.RequestUri.Port);
                    return new Threading.Tasks.ValueTask<IO.Stream>(new NetworkStream(socket, true));
                };
            }

            TestHelper.EnsureHttp2Feature(handler);

            if (useHttp2LoopbackServer)
            {
                handler.ServerCertificateCustomValidationCallback = TestHelper.AllowAllCertificates;
            }

            return handler;
        }

        protected static bool IsSocketsHttpHandler(HttpClientHandler handler) =>
            GetUnderlyingSocketsHttpHandler(handler) != null;

        protected static object GetUnderlyingSocketsHttpHandler(HttpClientHandler handler)
        {
            FieldInfo field = typeof(HttpClientHandler).GetField("_socketsHttpHandler", BindingFlags.Instance | BindingFlags.NonPublic);
            return field?.GetValue(handler);
        }

#if netcoreapp
        protected LoopbackServerFactory LoopbackServerFactory => UseHttp2LoopbackServer ? 
                                                                (LoopbackServerFactory)Http2LoopbackServerFactory.Singleton : 
                                                                (LoopbackServerFactory)Http11LoopbackServerFactory.Singleton;
#else
        protected LoopbackServerFactory LoopbackServerFactory => Http11LoopbackServerFactory.Singleton;
#endif
    }
}
