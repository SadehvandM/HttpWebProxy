using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Security;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;
using Titanium.Web.Proxy.Exceptions;
using System.Linq;
using HttpWebProxy.Utility.Filter;
using System.Data;
using Serilog.Core;

namespace HttpWebProxy
{
    public class ProxyController : IDisposable
    {
        //private readonly string constr = @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=D:\project\project\C#\prog\Work\HttpWebProxy\HttpWebProxy\HttpWebProxy\Data\HWPDb.mdf;Integrated Security=True";
        private readonly ProxyServer proxyServer;
        private ExplicitProxyEndPoint explicitEndPoint;
        private readonly BlackListChecker filtering = new BlackListChecker();
        private readonly Dictionary<HttpWebClient, SessionListItem> sessionDictionary =
            new Dictionary<HttpWebClient, SessionListItem>();
        public ObservableCollectionEx<SessionListItem> Sessions { get; } = new ObservableCollectionEx<SessionListItem>();

        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private CancellationToken cancellationToken => cancellationTokenSource.Token;
        private ConcurrentQueue<Tuple<ConsoleColor?, string>> consoleMessageQueue
            = new ConcurrentQueue<Tuple<ConsoleColor?, string>>();
        private readonly int logCommiteSecondsDelay = 10;
        private int lastSessionNumber;
        private Logger log;

        //private string[] fnames = new string[] { "bS", "h", "pId", "pr", "rDC", "sDC", "sC", "u", "cCId", "sCId" };
        //private string[] dfnames = new string[] { "bodySize", "host", "processId", "protocol", "receivedDataCount", "sentDataCount", "statusCode", "url", "clientConnectionId", "serverConnectionId" };
        //private DataTable dt = new DataTable();

        public ProxyController()
        {
            
            Task.Run(() => listenToConsole());

            proxyServer = new ProxyServer();

            //proxyServer.EnableHttp2 = true;

            // generate root certificate without storing it in file system
            //proxyServer.CertificateManager.CreateRootCertificate(false);

            //proxyServer.CertificateManager.TrustRootCertificate();
            //proxyServer.CertificateManager.TrustRootCertificateAsAdmin();

            proxyServer.ExceptionFunc = async exception =>
            {
                if (exception is ProxyHttpException phex)
                {
                    writeToConsole(exception.Message + ": " + phex.InnerException?.Message, ConsoleColor.Red);
                }
                else
                {
                    writeToConsole(exception.Message, ConsoleColor.Red);
                }
            };

            proxyServer.TcpTimeWaitSeconds = 10;
            proxyServer.ConnectionTimeOutSeconds = 15;
            proxyServer.ReuseSocket = false;
            proxyServer.EnableConnectionPool = false;
            proxyServer.ForwardToUpstreamGateway = true;
            proxyServer.CertificateManager.SaveFakeCertificates = true;
            proxyServer.ProxyBasicAuthenticateFunc = async (args, userName, password) =>
            {
                return userName == "ali" && password == "123";
            };

            // this is just to show the functionality, provided implementations use junk value
            //proxyServer.GetCustomUpStreamProxyFunc = onGetCustomUpStreamProxyFunc;
            //proxyServer.CustomUpStreamProxyFailureFunc = onCustomUpStreamProxyFailureFunc;

            // optionally set the Certificate Engine
            // Under Mono or Non-Windows runtimes only BouncyCastle will be supported
            //proxyServer.CertificateManager.CertificateEngine = Network.CertificateEngine.BouncyCastle;

            // optionally set the Root Certificate
            //proxyServer.CertificateManager.RootCertificate = new X509Certificate2("myCert.pfx", string.Empty, X509KeyStorageFlags.Exportable);
        }


        public void SetLogger(Logger logger)
        {
            log = logger;
        }

        public void StartProxy()
        {
            proxyServer.BeforeRequest += onRequest;
            proxyServer.BeforeResponse += onResponse;
            proxyServer.AfterResponse += onAfterResponse;

            proxyServer.ServerCertificateValidationCallback += OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback += OnCertificateSelection;


            explicitEndPoint = new ExplicitProxyEndPoint(IPAddress.Any, 8000);

            // Fired when a CONNECT request is received
            explicitEndPoint.BeforeTunnelConnectRequest += onBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse += onBeforeTunnelConnectResponse;

            // An explicit endpoint is where the client knows about the existence of a proxy
            // So client sends request in a proxy friendly manner
            proxyServer.AddEndPoint(explicitEndPoint);

            //proxyServer.ServerConnectionCountChanged += delegate
            //{
            //    Task.Run(() => { /*ServerConnectionCount = proxyServer.ServerConnectionCount;*/ });
            //};

            proxyServer.Start();


            foreach (var endPoint in proxyServer.ProxyEndPoints)
            {
                Console.WriteLine("Listening on '{0}' endpoint at Ip {1} and port: {2} ", endPoint.GetType().Name,
                    endPoint.IpAddress, endPoint.Port);
            }

            // Only explicit proxies can be set as system proxy!
            //proxyServer.SetAsSystemHttpProxy(explicitEndPoint);
            //proxyServer.SetAsSystemHttpsProxy(explicitEndPoint);
            if (RunTime.IsWindows)
            {
                proxyServer.SetAsSystemProxy(explicitEndPoint, ProxyProtocolType.AllHttp);
            }

            Task.Run(() => loggToDataStore());
        }

        public void Stop()
        {
            explicitEndPoint.BeforeTunnelConnectRequest -= onBeforeTunnelConnectRequest;
            explicitEndPoint.BeforeTunnelConnectResponse -= onBeforeTunnelConnectResponse;

            proxyServer.BeforeRequest -= onRequest;
            proxyServer.BeforeResponse -= onResponse;
            proxyServer.ServerCertificateValidationCallback -= OnCertificateValidation;
            proxyServer.ClientCertificateSelectionCallback -= OnCertificateSelection;

            proxyServer.Stop();

            // remove the generated certificates
            proxyServer.CertificateManager.RemoveTrustedRootCertificate();
        }

        private async Task onBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
        {
            string hostname = e.HttpClient.Request.RequestUri.Host;
            e.GetState().PipelineInfo.AppendLine(nameof(onBeforeTunnelConnectRequest) + ":" + hostname);
            writeToConsole("Tunnel to: " + hostname);

            var clientLocalIp = e.ClientLocalEndPoint.Address;
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }

            await Task.Run(() => { addSession(e); });
        }

        private void WebSocket_DataSent(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, true);
        }

        private void WebSocket_DataReceived(object sender, DataEventArgs e)
        {
            var args = (SessionEventArgs)sender;
            WebSocketDataSentReceived(args, e, false);
        }

        private void WebSocketDataSentReceived(SessionEventArgs args, DataEventArgs e, bool sent)
        {
            var color = sent ? ConsoleColor.Green : ConsoleColor.Blue;

            foreach (var frame in args.WebSocketDecoderReceive.Decode(e.Buffer, e.Offset, e.Count))
            {
                if (frame.OpCode == WebsocketOpCode.Binary)
                {
                    var data = frame.Data.ToArray();
                    string str = string.Join(",", data.ToArray().Select(x => x.ToString("X2")));
                    writeToConsole(str, color);
                }

                if (frame.OpCode == WebsocketOpCode.Text)
                {
                    writeToConsole(frame.GetText(), color);
                }
            }
        }

        private Task onBeforeTunnelConnectResponse(object sender, TunnelConnectSessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onBeforeTunnelConnectResponse) + ":" + e.HttpClient.Request.RequestUri);

            if (sessionDictionary.TryGetValue(e.HttpClient, out var item))
            {
                item.Update(e);
            }

            return Task.CompletedTask;
        }

        // intercept & cancel redirect or update requests
        private async Task onRequest(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onRequest) + ":" + e.HttpClient.Request.RequestUri);


            var clientLocalIp = e.ClientLocalEndPoint.Address;
            // if client ip address is loopback address(127.0.0.1)
            if (!clientLocalIp.Equals(IPAddress.Loopback) && !clientLocalIp.Equals(IPAddress.IPv6Loopback))
            {
                e.HttpClient.UpStreamEndPoint = new IPEndPoint(clientLocalIp, 0);
            }

            writeToConsole("Active Client Connections:" + ((ProxyServer)sender).ClientConnectionCount);
            writeToConsole(e.HttpClient.Request.Url);


            //To cancel a request with a custom HTML content
            //Filter URL
            if (filtering.existInBlackList(e.HttpClient.Request.RequestUri.AbsoluteUri))
            {
                e.Ok("<!DOCTYPE html>" +
                      "<html><body><h1>" +
                      "Url Blocked" +
                      "</h1>" +
                      "<p>Url " + e.HttpClient.Request.Url + " was blocked by administration.</p>" +
                      "</body>" +
                      "</html>");


                SessionListItem item = null;
                await Task.Run(() => { item = addSession(e, true); });
            }
            else
            {

                SessionListItem item = null;
                await Task.Run(() => { item = addSession(e); });
            }

        }

        private async Task onResponse(object sender, SessionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(onResponse));

            // some web site use websocket example is smatkets.com
            if (e.HttpClient.ConnectRequest?.TunnelType == TunnelType.Websocket)
            {
                e.DataSent += WebSocket_DataSent;
                e.DataReceived += WebSocket_DataReceived;
            }

            writeToConsole("Active Server Connections:" + ((ProxyServer)sender).ServerConnectionCount);

            SessionListItem item = null;
            await Task.Run(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out item))
                {
                    item.Update(e);
                }
            });
        }

        private async Task onAfterResponse(object sender, SessionEventArgs e)
        {
            writeToConsole($"Pipelineinfo: {e.GetState().PipelineInfo}", ConsoleColor.Yellow);

            await Task.Run(() =>
            {
                if (sessionDictionary.TryGetValue(e.HttpClient, out var item))
                {
                    item.Exception = e.Exception;
                }
            });
        }

        /// <summary>
        ///     Allows overriding default certificate validation logic
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateValidation(object sender, CertificateValidationEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateValidation));

            // set IsValid to true/false based on Certificate Errors
            if (e.SslPolicyErrors == SslPolicyErrors.None)
            {
                e.IsValid = true;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///     Allows overriding default client certificate selection logic during mutual authentication
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public Task OnCertificateSelection(object sender, CertificateSelectionEventArgs e)
        {
            e.GetState().PipelineInfo.AppendLine(nameof(OnCertificateSelection));

            // set e.clientCertificate to override

            return Task.CompletedTask;
        }

        private void writeToConsole(string message, ConsoleColor? consoleColor = null)
        {
            consoleMessageQueue.Enqueue(new Tuple<ConsoleColor?, string>(consoleColor, message));
        }

        private async Task listenToConsole()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (consoleMessageQueue.TryDequeue(out var item))
                {
                    var consoleColor = item.Item1;
                    var message = item.Item2;

                    if (consoleColor.HasValue)
                    {
                        ConsoleColor existing = Console.ForegroundColor;
                        Console.ForegroundColor = consoleColor.Value;
                        Console.WriteLine(message);
                        Console.ForegroundColor = existing;
                    }
                    else
                    {
                        Console.WriteLine(message);
                    }
                }

                //reduce CPU usage
                await Task.Delay(50);
            }
        }

        private async Task loggToDataStore()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                //int j = 0;
                //while (j < Sessions.Count )
                //{
                //    using (SqlConnection con = new SqlConnection(connectionString: constr))
                //    {
                        
                //        for (int i = j; i < Sessions.Count; i++)
                //        {
                //            using (SqlCommand cmd = new SqlCommand(cmdText: "INSERT INTO  HttpWebProxySessionLog(bodySize,host,processId,protocol,receivedDataCount,sentDataCount,statusCode,url,clientConnectionId,serverConnectionId) Values(@bS,@h,@pId,@pr,@rDC,@sDC,@sC,@u,@cCId,@sCId)", con))
                //            {
                //                cmd.Parameters.AddWithValue("@bS", Sessions[i].BodySize);
                //                cmd.Parameters.AddWithValue("@h", Sessions[i].Host);
                //                cmd.Parameters.AddWithValue("@pId", Sessions[i].ProcessId);
                //                cmd.Parameters.AddWithValue("@pr", Sessions[i].Process);
                //                cmd.Parameters.AddWithValue("@rDC", Sessions[i].ReceivedDataCount);
                //                cmd.Parameters.AddWithValue("@sDC", Sessions[i].SentDataCount);
                //                cmd.Parameters.AddWithValue("@sC", Sessions[i].StatusCode);
                //                cmd.Parameters.AddWithValue("@u", Sessions[i].Url);
                //                cmd.Parameters.AddWithValue("@cCId", Sessions[i].ClientConnectionId);
                //                cmd.Parameters.AddWithValue("@sCId", Sessions[i].ServerConnectionId);
                //                con.Open();
                //                int k = cmd.ExecuteNonQuery();
                //                if (k != 0)
                //                {
                //                    //lblmsg.Text = "Record Inserted Succesfully into the Database";
                //                    //lblmsg.ForeColor = System.Drawing.Color.CornflowerBlue;
                //                }
                //                con.Close();
                //            }
                //        }
                //        j += 10;
                //    }
                //}

                //reduce CPU usage
                await Task.Delay(TimeSpan.FromSeconds(logCommiteSecondsDelay));
            }
            
        }

        private SessionListItem addSession(SessionEventArgsBase e, bool isBlocked = false)
        {
            try
            {
                var item = createSessionListItem(e);
                item.IsBlocked = isBlocked;

                Sessions.Add(item);
                sessionDictionary.TryAdd(e.HttpClient, item);
                return item;
            }
            catch (Exception x)
            {

                return null;
            }
            
            
        }

        private SessionListItem createSessionListItem(SessionEventArgsBase e)
        {
            lastSessionNumber++;
            bool isTunnelConnect = e is TunnelConnectSessionEventArgs;
            var item = new SessionListItem
            {
                Number = lastSessionNumber,
                ClientConnectionId = e.ClientConnectionId,
                ServerConnectionId = e.ServerConnectionId,
                HttpClient = e.HttpClient,
                ClientRemoteEndPoint = e.ClientRemoteEndPoint,
                ClientLocalEndPoint = e.ClientLocalEndPoint,
                IsTunnelConnect = isTunnelConnect,
                IsBlocked = false,
                LastmodifyTime = DateTime.Now,
                updated = true
            };

            //if (isTunnelConnect || e.HttpClient.Request.UpgradeToWebSocket)
            e.DataReceived += (sender, args) =>
            {
                var session = (SessionEventArgsBase)sender;
                if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                {
                    var connectRequest = session.HttpClient.ConnectRequest;
                    var tunnelType = connectRequest?.TunnelType ?? TunnelType.Unknown;
                    if (tunnelType != TunnelType.Unknown)
                    {
                        li.Protocol = TunnelTypeToString(tunnelType);
                    }

                    li.ReceivedDataCount += args.Count;

                }
            };

            e.DataSent += (sender, args) =>
            {
                var session = (SessionEventArgsBase)sender;
                if (sessionDictionary.TryGetValue(session.HttpClient, out var li))
                {
                    var connectRequest = session.HttpClient.ConnectRequest;
                    var tunnelType = connectRequest?.TunnelType ?? TunnelType.Unknown;
                    if (tunnelType != TunnelType.Unknown)
                    {
                        li.Protocol = TunnelTypeToString(tunnelType);
                    }

                    li.SentDataCount += args.Count;

                }
            };

            item.Update(e);
            return item;
        }

        private string TunnelTypeToString(TunnelType tunnelType)
        {
            switch (tunnelType)
            {
                case TunnelType.Https:
                    return "https";
                case TunnelType.Websocket:
                    return "websocket";
                case TunnelType.Http2:
                    return "http2";
            }

            return null;
        }

        public void Dispose()
        {
            cancellationTokenSource.Dispose();
            proxyServer.Dispose();
        }
    }

}
