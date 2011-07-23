using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

using System.Threading;
using System.ComponentModel;

using Lidgren.Network;

namespace Microsoft.Xna.Framework.Net
{
	internal class MonoGamerPeer
	{
		private BackgroundWorker MGServerWorker = new BackgroundWorker ();
		bool done = false;
		NetServer peer;
		NetworkSession session;
		AvailableNetworkSession availableSession;
		string myLocalAddress = string.Empty;
		IPEndPoint myLocalEndPoint = null;
		Dictionary<long, NetConnection> pendingGamers = new Dictionary<long, NetConnection>();
		//Dictionary<long, NetConnection> connectedGamers = new Dictionary<long, NetConnection>();
        bool online = false;
        private static int port = 3074;
        private static IPEndPoint m_masterServer;
        private static int masterserverport = 6000;
        private static string masterServer = "monolive.servegame.com";
        private static string applicationIdentifier = "monogame";
		
		public MonoGamerPeer(NetworkSession session, AvailableNetworkSession availableSession)
        {            
            this.session = session;
            this.online = this.session.SessionType == NetworkSessionType.PlayerMatch;
            this.availableSession = availableSession;            
            //MGServerWorker.WorkerReportsProgress = true;
            MGServerWorker.WorkerSupportsCancellation = true;
            MGServerWorker.DoWork += new DoWorkEventHandler(MGServer_DoWork);
            MGServerWorker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(MGServer_RunWorkerCompleted);
            MGServerWorker.RunWorkerAsync();

            HookEvents();
        }
		
		private void HookEvents()
		{
			session.GameEnded += HandleSessionStateChanged;
			
			session.SessionEnded += HandleSessionStateChanged;
			
			session.GameStarted += HandleSessionStateChanged;		
			
		}

		void HandleSessionStateChanged (object sender, EventArgs e)
		{
			Console.WriteLine("session state change");
			SendSessionStateChange();
			
			if (session.SessionState == NetworkSessionState.Ended)
				MGServerWorker.CancelAsync();			
		}

		internal void ShutDown() {
			MGServerWorker.CancelAsync();			
		}
		
		private void MGServer_DoWork (object sender, DoWorkEventArgs e)
		{
			BackgroundWorker worker = sender as BackgroundWorker;

            NetPeerConfiguration config = new NetPeerConfiguration(applicationIdentifier);
			config.EnableMessageType (NetIncomingMessageType.DiscoveryRequest);
			config.EnableMessageType (NetIncomingMessageType.DiscoveryResponse);
            config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
			
			if (availableSession == null)
                config.Port = port;

			// create and start server
			peer = new NetServer (config);
			peer.Start ();
			
			myLocalAddress = GetMyLocalIpAddress();
			myLocalEndPoint = ParseIPEndPoint(myLocalAddress + ":" + peer.Port);

            if (availableSession != null)
            {
                if (!this.online)
                {
                    peer.Connect(availableSession.EndPoint);
                }
                else
                {
                    RequestNATIntroduction(availableSession.EndPoint, peer);                    
                }
            }
            else
            {
                if (this.online)
                {
                    IPAddress ipaddr = NetUtility.Resolve(masterServer);
                    if (ipaddr != null)
                    {
                        m_masterServer = new IPEndPoint(ipaddr, masterserverport);
                        LocalNetworkGamer localMe = session.LocalGamers[0];

                        NetOutgoingMessage om = peer.CreateMessage();

                        om.Write((byte)0);
                        om.Write(session.AllGamers.Count);
                        om.Write(localMe.Gamertag);
                        om.Write(session.PrivateGamerSlots);
                        om.Write(session.MaxGamers);
                        om.Write(localMe.IsHost);
                        IPAddress adr = IPAddress.Parse(GetMyLocalIpAddress());
                        om.Write(new IPEndPoint(adr, port));
                        om.Write(peer.Configuration.AppIdentifier);
                        peer.SendUnconnectedMessage(om, m_masterServer); // send message to peer
                    }
                    else
                    {
                        throw new Exception("Could not resolve live host");
                    }
                }
            }
			
			// run until we are done
			do {
				
				NetIncomingMessage msg;
				while ((msg = peer.ReadMessage ()) != null) {
					
					switch (msg.MessageType) {
                    case NetIncomingMessageType.UnconnectedData :
                        break;
                    case NetIncomingMessageType.NatIntroductionSuccess:
                        Console.WriteLine("NAT punch through OK " + msg.SenderEndpoint);                            
                        peer.Connect(msg.SenderEndpoint);                            
                        break;
					case NetIncomingMessageType.DiscoveryRequest:
						//
						// Server received a discovery request from a client; send a discovery response (with no extra data attached)
						//
						// Get the primary local gamer
						LocalNetworkGamer localMe = session.LocalGamers[0];
						
						NetOutgoingMessage om = peer.CreateMessage ();
						
						om.Write(session.RemoteGamers.Count);
						om.Write(localMe.Gamertag);
						om.Write(session.PrivateGamerSlots);
						om.Write(session.MaxGamers);
						om.Write(localMe.IsHost);
						int[] propertyData = new int[session.SessionProperties.Count * 2];
						NetworkSessionProperties.WriteProperties(session.SessionProperties, propertyData);
						for (int x = 0; x < propertyData.Length; x++) {
							om.Write(propertyData[x]);
						}

						peer.SendDiscoveryResponse (om, msg.SenderEndpoint);
						break;
					case NetIncomingMessageType.VerboseDebugMessage:
					case NetIncomingMessageType.DebugMessage:
					case NetIncomingMessageType.WarningMessage:
					case NetIncomingMessageType.ErrorMessage:
						//
						// Just print diagnostic messages to console
						//
						Console.WriteLine (msg.ReadString ());
						break;
					case NetIncomingMessageType.StatusChanged:
						NetConnectionStatus status = (NetConnectionStatus)msg.ReadByte ();
						if (status == NetConnectionStatus.Disconnected) {
							Console.WriteLine (NetUtility.ToHexString (msg.SenderConnection.RemoteUniqueIdentifier) + " disconnected! from " + msg.SenderEndpoint);
							CommandGamerLeft cgj = new CommandGamerLeft(msg.SenderConnection.RemoteUniqueIdentifier);
							CommandEvent cmde = new CommandEvent(cgj);
							session.commandQueue.Enqueue(cmde);					
						}
						if (status == NetConnectionStatus.Connected) {
							//
							// A new player just connected!
							//
                            if (!pendingGamers.ContainsKey(msg.SenderConnection.RemoteUniqueIdentifier))
                            {
                                Console.WriteLine(NetUtility.ToHexString(msg.SenderConnection.RemoteUniqueIdentifier) + " connected! from " + msg.SenderEndpoint);
                                pendingGamers.Add(msg.SenderConnection.RemoteUniqueIdentifier, msg.SenderConnection);
                                SendProfileRequest(msg.SenderConnection);
                            }
                            else
                            {
                                Console.WriteLine("Already have a connection for that user, this is probably due to both NAT intro requests working");
                            }
						}

						break;
						
					case NetIncomingMessageType.Data:
						
						NetworkMessageType mt = (NetworkMessageType)msg.ReadByte();
						switch (mt) {
						case NetworkMessageType.Data:
							byte[] data = new byte[msg.LengthBytes - 1];
							msg.ReadBytes(data, 0, data.Length);
							CommandEvent cme = new CommandEvent(new CommandReceiveData(msg.SenderConnection.RemoteUniqueIdentifier,
												data));
							session.commandQueue.Enqueue(cme);						
							break;
						case NetworkMessageType.Introduction:
							
							var introductionAddress = msg.ReadString();
							
							try {
								IPEndPoint endPoint = ParseIPEndPoint(introductionAddress);
							
								if (myLocalEndPoint.ToString() != endPoint.ToString() && !AlreadyConnected(endPoint)) {
									
									Console.WriteLine("Received Introduction for: " + introductionAddress + 
									" and I am: " + myLocalEndPoint + " from: " + msg.SenderEndpoint);

									peer.Connect (endPoint);
								}
							}
							catch (Exception exc) {
								Console.WriteLine("Error parsing Introduction: " + introductionAddress + " : " + exc.Message);
							}
							
							break;
						case NetworkMessageType.GamerProfile:
							//Console.WriteLine("Profile recieved from: " + NetUtility.ToHexString(msg.SenderConnection.RemoteUniqueIdentifier));
							if (pendingGamers.ContainsKey(msg.SenderConnection.RemoteUniqueIdentifier)) {
								pendingGamers.Remove(msg.SenderConnection.RemoteUniqueIdentifier);
								msg.ReadInt32();
								string gamerTag = msg.ReadString();
								msg.ReadInt32();
								msg.ReadInt32();
								GamerStates state = (GamerStates)msg.ReadInt32();
								state &= ~GamerStates.Local;
								CommandGamerJoined cgj = new CommandGamerJoined(msg.SenderConnection.RemoteUniqueIdentifier);
								cgj.GamerTag = gamerTag;
								cgj.State = state;
								CommandEvent cmde = new CommandEvent(cgj);
								session.commandQueue.Enqueue(cmde);					
							}
							else {
								Console.WriteLine("We received a profile for an existing gamer.  Need to update it.");
							}
							break;
						case NetworkMessageType.RequestGamerProfile:
							//Console.WriteLine("Profile Request recieved from: " + msg.SenderEndpoint);
							SendProfile(msg.SenderConnection);
							break;	
						case NetworkMessageType.GamerStateChange:
							GamerStates gamerstate = (GamerStates)msg.ReadInt32();
                            gamerstate &= ~GamerStates.Local;
                            Console.WriteLine("State Change from: " + msg.SenderEndpoint + " new State: " + gamerstate);
							foreach (var gamer in session.RemoteGamers) {
								if (gamer.RemoteUniqueIdentifier == msg.SenderConnection.RemoteUniqueIdentifier)
                                    gamer.State = gamerstate;
							}
							break;								
						case NetworkMessageType.SessionStateChange:
							NetworkSessionState sessionState = (NetworkSessionState)msg.ReadInt32();

							foreach (var gamer in session.RemoteGamers) {
								if (gamer.RemoteUniqueIdentifier == msg.SenderConnection.RemoteUniqueIdentifier) {
									Console.WriteLine("Session State change from: " + NetUtility.ToHexString (msg.SenderConnection.RemoteUniqueIdentifier) +
										" session is now: " + sessionState);
									if (gamer.IsHost && sessionState == NetworkSessionState.Playing) {
										session.StartGame();
									}
									
								}
							}
							
							break;								
						}						
						break;
					}

				}

				// sleep to allow other processes to run smoothly
				// This may need to be changed depending on network throughput
				Thread.Sleep (1);
				
				if (worker.CancellationPending) {
					Console.WriteLine("worker CancellationPending");
					e.Cancel = true;
					done = true;
				}
			} while (!done);
		}

		private bool AlreadyConnected (IPEndPoint endPoint)
		{
			foreach (NetConnection player in peer.Connections) {
				if (player.RemoteEndpoint == endPoint) {
					return true;
				}
			}
			
			return false;
		}
		
		private void MGServer_RunWorkerCompleted (object sender, RunWorkerCompletedEventArgs e)
		{
			if ((e.Cancelled == true)) {
				Console.WriteLine("Canceled");

			} else if (!(e.Error == null)) {
				Console.WriteLine("Error: " + e.Error.Message);
			} 
			Console.WriteLine("worker Completed");

            if (online && this.availableSession == null)
            {
                // inform the master server we have closed
                NetOutgoingMessage om = peer.CreateMessage();

                om.Write((byte)3);
                om.Write(this.session.Host.Gamertag);
                om.Write(peer.Configuration.AppIdentifier);
                peer.SendUnconnectedMessage(om, m_masterServer); // send message to peer
            }
			peer.Shutdown ("app exiting");
		}	
		
		internal void SendProfile(NetConnection player) {
			NetOutgoingMessage om = peer.CreateMessage ();
			om.Write((byte)NetworkMessageType.GamerProfile);
			om.Write(session.AllGamers.Count);
			om.Write(session.LocalGamers[0].Gamertag);
			om.Write(session.PrivateGamerSlots);
			om.Write(session.MaxGamers);
			om.Write((int)session.LocalGamers[0].State);			
			Console.WriteLine("Sent profile to: " + NetUtility.ToHexString(player.RemoteUniqueIdentifier));
			peer.SendMessage(om, player, NetDeliveryMethod.ReliableOrdered);			
		}
		
		internal void SendProfileRequest(NetConnection player) {
			NetOutgoingMessage om = peer.CreateMessage ();
			om.Write((byte)NetworkMessageType.RequestGamerProfile);
			Console.WriteLine("Sent profile request to: " + NetUtility.ToHexString(player.RemoteUniqueIdentifier));
			peer.SendMessage(om, player, NetDeliveryMethod.ReliableOrdered);			
		}
		
		internal void SendPeerIntroductions(NetworkGamer gamer) {
			
			NetConnection playerConnection = null;
			
			foreach (NetConnection player in peer.Connections) {
				if (player.RemoteUniqueIdentifier == gamer.RemoteUniqueIdentifier) {
					playerConnection = player;
				}
			}
			
			if (playerConnection == null) {
				return;
			}
			
			foreach (NetConnection player in peer.Connections) {
				
				Console.WriteLine("Introduction sent to: " + player.RemoteEndpoint);
				NetOutgoingMessage om = peer.CreateMessage ();
				om.Write((byte)NetworkMessageType.Introduction);
				om.Write(playerConnection.RemoteEndpoint.ToString()); 

				peer.SendMessage(om, player, NetDeliveryMethod.ReliableOrdered);
			}
		}

		internal void SendGamerStateChange(NetworkGamer gamer) {
			
			NetOutgoingMessage om = peer.CreateMessage();
			om.Write((byte)NetworkMessageType.GamerStateChange);
			om.Write((int)gamer.State);
			
			SendMessage(om, SendDataOptions.Reliable, gamer);
		}
		
		internal void SendSessionStateChange() {
			
			NetOutgoingMessage om = peer.CreateMessage();
			om.Write((byte)NetworkMessageType.SessionStateChange);
			om.Write((int)session.SessionState);
			
			SendMessage(om, SendDataOptions.Reliable, null);
		}		
		
		public static IPEndPoint ParseIPEndPoint(string endPoint)
		{
			string[] ep = endPoint.Split(':');
			if(ep.Length != 2) throw new FormatException("Invalid endpoint format");
			IPAddress ip;
			if(!IPAddress.TryParse(ep[0], out ip))
			{
				throw new FormatException("Invalid ip-adress");
			}
			int port;
			if(!int.TryParse(ep[1], out port))
			{
				throw new FormatException("Invalid port");
			}
			return new IPEndPoint(ip, port);
		}
		
		internal static string GetMyLocalIpAddress () {
			
			IPHostEntry host;
			string localIP = "?";
			host = Dns.GetHostEntry(Dns.GetHostName());
			foreach (IPAddress ip in host.AddressList)
			{
				// We only want those of type InterNetwork
				if (ip.AddressFamily == AddressFamily.InterNetwork)
				{
					// We will return the first one in the list
					localIP = ip.ToString();
					break;
				}
			}
			return localIP;
		}
		
		internal void DiscoverPeers() 
		{
			peer.DiscoverLocalPeers(port);			
		}
		
		internal void SendData (
			byte[] data,
			SendDataOptions options)
		{
			this.SendMessage(NetworkMessageType.Data, data, options, null);
		}
		
		internal void SendData (
			byte[] data,
			SendDataOptions options,
			NetworkGamer gamer)
		{
			this.SendMessage(NetworkMessageType.Data, data, options, gamer);
		}
		
		private void SendMessage (NetworkMessageType messageType, byte[] data, SendDataOptions options, NetworkGamer gamer) 
		{

			NetOutgoingMessage om = peer.CreateMessage ();

			om.Write((byte)messageType);
			om.Write (data);
			
			SendMessage(om, options,gamer);

		}
		
		private void SendMessage (NetOutgoingMessage om, SendDataOptions options, NetworkGamer gamer) 
		{
			//Console.WriteLine("Data to send: " + data.Length);
			
//			foreach (NetConnection player in server.Connections) {
//				// ... send information about every other player (actually including self)
//				foreach (NetConnection otherPlayer in server.Connections) {
					
//					if (gamer != null && gamer.RemoteUniqueIdentifier != otherPlayer.RemoteUniqueIdentifier) {
//						continue;
//					}

					NetDeliveryMethod ndm = NetDeliveryMethod.Unreliable;
					switch (options) {
					case SendDataOptions.Reliable:
						ndm = NetDeliveryMethod.ReliableSequenced;
						break;
					case SendDataOptions.ReliableInOrder:
						ndm = NetDeliveryMethod.ReliableOrdered;
						break;
					case SendDataOptions.InOrder:
						ndm = NetDeliveryMethod.UnreliableSequenced;
						break;
					case SendDataOptions.None:
						ndm = NetDeliveryMethod.Unknown;
						break;
					}
					// send message
					//server.SendToAll (om, player, ndm);
					peer.SendToAll (om, ndm);

//				}
//			}				
		}		
		
		static NetPeer netPeer;
		static List<NetIncomingMessage> discoveryMsgs;
		
		internal static void Find(NetworkSessionType sessionType) {

            NetPeerConfiguration config = new NetPeerConfiguration(applicationIdentifier);			
            if (sessionType == NetworkSessionType.PlayerMatch)
            {
                config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
                config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            }
            else
            {
                config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
            }
			netPeer = new NetPeer(config);
			
			netPeer.Start();

            if (sessionType == NetworkSessionType.PlayerMatch)
            {
                GetServerList(netPeer);
            }
            else
            {
                netPeer.DiscoverLocalPeers(port);
            }
			
			DateTime now = DateTime.Now;
			
			discoveryMsgs = new List<NetIncomingMessage>();
			
			do {
				NetIncomingMessage msg;
				while((msg = netPeer.ReadMessage()) != null) {
					switch (msg.MessageType)
					{
					case NetIncomingMessageType.DiscoveryResponse:
						discoveryMsgs.Add(msg);
						break;
                    case NetIncomingMessageType.UnconnectedData:
                        if (msg.SenderEndpoint.Equals(m_masterServer))
                        {
                            discoveryMsgs.Add(msg);
                            /*
                             * // it's from the master server - must be a host
                            IPEndPoint hostInternal = msg.ReadIPEndpoint();
                            IPEndPoint hostExternal = msg.ReadIPEndpoint();

                            m_hostList.Add(new IPEndPoint[] { hostInternal, hostExternal });                            
                             */
                        }
                        break;
					case NetIncomingMessageType.VerboseDebugMessage:
					case NetIncomingMessageType.DebugMessage:
					case NetIncomingMessageType.WarningMessage:
					case NetIncomingMessageType.ErrorMessage:
						//
						// Just print diagnostic messages to console
						//
						Console.WriteLine ("Find: " + msg.ReadString ());
						break;
					}
				}
			} while ((DateTime.Now - now).Seconds <= 2);
			

			netPeer.Shutdown("Find shutting down");
		}

        /// <summary>
        /// Contacts the Master Server on the net and gets a list of available host games
        /// </summary>
        /// <param name="netPeer"></param>
        private static void GetServerList(NetPeer netPeer)
        {
            m_masterServer = new IPEndPoint(NetUtility.Resolve(masterServer), masterserverport);

            NetOutgoingMessage listRequest = netPeer.CreateMessage();
            listRequest.Write((byte)1);
            listRequest.Write(netPeer.Configuration.AppIdentifier);
            netPeer.SendUnconnectedMessage(listRequest, m_masterServer);

        }

        public static void RequestNATIntroduction(IPEndPoint host, NetPeer peer)
        {
            if (host == null)
            {
                return;
            }

            if (m_masterServer == null)
                throw new Exception("Must connect to master server first!");

            NetOutgoingMessage om = peer.CreateMessage();
            om.Write((byte)2); // NAT intro request

            // write internal ipendpoint
            IPAddress addr = IPAddress.Parse(GetMyLocalIpAddress());
            om.Write(new IPEndPoint(addr, peer.Port));

            // write external address of host to request introduction to
            IPEndPoint hostEp = new IPEndPoint(host.Address, port);
            om.Write(hostEp);
            om.Write(peer.Configuration.AppIdentifier); // send the app id

            peer.SendUnconnectedMessage(om, m_masterServer);
        }

		
		internal static void FindResults (List<AvailableNetworkSession> networkSessions) {
			
			foreach (NetIncomingMessage im in discoveryMsgs) {

                AvailableNetworkSession available = new AvailableNetworkSession();
                switch (im.MessageType)
                {
                    case NetIncomingMessageType.DiscoveryResponse :                        
				            int currentGameCount = im.ReadInt32();
				            string gamerTag = im.ReadString();
				            int openPrivateGamerSlots = im.ReadInt32();
				            int openPublicGamerSlots = im.ReadInt32();
				            bool isHost = im.ReadBoolean();
					
					NetworkSessionProperties properties = new NetworkSessionProperties();
					int[] propertyData = new int[properties.Count * 2];
					for (int x = 0; x < propertyData.Length; x++) {
						propertyData[x] = im.ReadInt32();
					}
					
					NetworkSessionProperties.ReadProperties(properties, propertyData);
					available.SessionProperties = properties;
						
                            available.SessionType = NetworkSessionType.SystemLink;
				            available.CurrentGamerCount = currentGameCount;
				            available.HostGamertag = gamerTag;
				            available.OpenPrivateGamerSlots = openPrivateGamerSlots;
				            available.OpenPublicGamerSlots = openPublicGamerSlots;
				            available.EndPoint = im.SenderEndpoint;
                            available.InternalEndpont = null;
                        break;
                    case NetIncomingMessageType.UnconnectedData :
                        if (im.SenderEndpoint.Equals(m_masterServer))
                        {
                            currentGameCount = im.ReadInt32();
                            gamerTag = im.ReadString();
                            openPrivateGamerSlots = im.ReadInt32();
                            openPublicGamerSlots = im.ReadInt32();
                            isHost = im.ReadBoolean();
                            IPEndPoint hostInternal = im.ReadIPEndpoint();
                            IPEndPoint hostExternal = im.ReadIPEndpoint();
                            available.SessionType = NetworkSessionType.PlayerMatch;
                            available.CurrentGamerCount = currentGameCount;
                            available.HostGamertag = gamerTag;
                            available.OpenPrivateGamerSlots = openPrivateGamerSlots;
                            available.OpenPublicGamerSlots = openPublicGamerSlots;
                            // its data from the master server so it includes the internal and external endponts
                            available.EndPoint = hostExternal;
                            available.InternalEndpont = hostInternal;
                        }
                        break;
                }
				
				
				networkSessions.Add(available);
				
			}
		}
	}
}
