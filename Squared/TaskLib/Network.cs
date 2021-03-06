﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Net.Sockets;

namespace Squared.Task {
    public static class Network {
        public static Future<TcpClient> ConnectTo (string host, int port) {
            var f = new Future<TcpClient>();
            TcpClient client = new TcpClient();
            client.BeginConnect(host, port, (ar) => {
                try {
                    client.EndConnect(ar);
                    f.Complete(client);
                } catch (FutureHandlerException) {
                    throw;
                } catch (Exception ex) {
                    f.Fail(ex);
                    client.Close();
                }
            }, null);
            return f;
        }
    }

    public static class NetworkExtensionMethods {
        public static Future<TcpClient> AcceptIncomingConnection (this TcpListener listener) {
            var f = new Future<TcpClient>();
            listener.BeginAcceptTcpClient((ar) => {
                try {
                    TcpClient result = listener.EndAcceptTcpClient(ar);
                    f.Complete(result);
                } catch (FutureHandlerException) {
                    throw;
                } catch (Exception ex) {
                    f.Fail(ex);
                }
            }, null);
            return f;
        }
    }
}
