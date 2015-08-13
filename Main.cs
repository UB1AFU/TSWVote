﻿using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace TSWVote
{
	[ApiVersion(1, 21)]
	public class TSWVote : TerrariaPlugin
	{
		public override string Name
		{
			get { return "TServerWebVote"; }
		}

		public override string Author
		{
			get { return "Community"; }
		}

		public override string Description
		{
			get { return "A plugin to vote to TServerWeb in-game."; }
		}

		public override Version Version
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}

		private Dictionary<string, VoteIP> IPs = new Dictionary<string, VoteIP>();

		private ConcurrentQueue<VoteWC> webClientQueue;

		internal static Config TSWConfig { get; private set; }

		public TSWVote(Main game)
			: base(game)
		{
			Order = 1000;
			WebRequest.DefaultWebProxy = null;

			TSWConfig = Config.Read();

			webClientQueue = new ConcurrentQueue<VoteWC>();
			for (int i = 0; i < TSWConfig.NumberOfWebClients; i++)
			{
				VoteWC webClient = new VoteWC();
				webClient.DownloadStringCompleted += WebClient_DownloadStringCompleted;
				webClientQueue.Enqueue(webClient);
            }
		}

		public override void Initialize()
		{
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize, -1);
			ServerApi.Hooks.ServerChat.Register(this, OnChat, 1000);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.ServerChat.Deregister(this, OnChat);

				if (webClientQueue != null)
				{
					VoteWC webClient;
					while (this.webClientQueue.TryDequeue(out webClient))
					{
						webClient.DownloadStringCompleted -= WebClient_DownloadStringCompleted;
						webClient.Dispose();
					}
				}
				 
			}
			base.Dispose(disposing);
		}

		private void OnChat(ServerChatEventArgs args)
		{
			if (!args.Text.StartsWith("/vote"))
				return;

			var player = TShock.Players[args.Who];

			if (player == null)
			{
				args.Handled = true;
				return;
			}

			Match M = Regex.Match(args.Text, "^/vote(?: (.*))?$", RegexOptions.IgnoreCase);
			if (M.Success)
			{
				if (TSWConfig.RequirePermission && !player.Group.HasPermission(TSWConfig.PermissionName))
				{
					player.SendSuccessMessage("[TServerWeb] You don't have permission to vote!");
					return;
				}

				CommandArgs e = new CommandArgs(args.Text, player, new List<string>());
				string Args = M.Groups[1].Value;

				if (!IPs.ContainsKey(player.IP)) IPs.Add(player.IP, new VoteIP(DateTime.Now));

				if (!string.IsNullOrWhiteSpace(Args))
				{
					e.Parameters.Add(Args);
					TSPlayer.Server.SendMessage(player.Name + " has entered /vote captcha.", 255, 255, 255);
					Vote(e);
					args.Handled = true;
					return;
				}

				TSPlayer.Server.SendMessage(player.Name + " executed: /vote.", 255, 255, 255);
				Vote(e);
				args.Handled = true;
			}
		}

		private void OnInitialize(EventArgs args)
		{
			try
			{
				TSWConfig = Config.Read();
			}
			catch
			{
				SendError("Config", "Config file is broken! TSWVote plugin will not load!");
				return;
			}

			int id;
			string message;
			if (!GetServerID(out id, out message))
				SendError("Configuration", message);

			if (TShock.Config.RestApiEnabled == false)
			{
				SendError("REST API", "REST API Not Enabled! TSWVote plugin will not load!");
				return;
			}

			Commands.ChatCommands.Add(new Command(TSWConfig.RequirePermission ? TSWConfig.PermissionName : null, delegate { }, "vote"));
			// ^ We're making sure the command can be seen in /help. It does nothing though.

			Commands.ChatCommands.Add(new Command("vote.changeid", ChangeID, "tserverweb"));

			Commands.ChatCommands.Add(new Command("vote.checkversion", CheckVersion, "tswversioncheck"));

			Commands.ChatCommands.Add(new Command("vote.admin", Clear, "voteclear"));
		}

		private void Clear(CommandArgs e)
		{
			if (e.Parameters.Count == 0)
			{
				if (IPs.ContainsKey(e.Player.IP) && IPs[e.Player.IP].State != VoteState.None)
				{
					e.Player.SendSuccessMessage(string.Format("[TServerWeb] Removed state {0} from IP {1} succesfully.", IPs[e.Player.IP].State, e.Player.IP));
					IPs.Remove(e.Player.IP);
				}
				else
					e.Player.SendSuccessMessage(string.Format("[TServerWeb] No state for IP {0} found.", e.Player.IP));
				return;
			}

			string Target = string.Join(" ", e.Parameters).ToLower();

			if (Target == "all")
			{
				IPs.Clear();
				e.Player.SendSuccessMessage("[TServerWeb] Reset all votestates!");
				return;
			}

			List<TSPlayer> Ts = TShock.Players.Where(p => (p != null && p.Active && p.Name.ToLower().StartsWith(Target))).ToList();

			if (Ts.Count == 0)
			{
				e.Player.SendSuccessMessage("[TServerWeb] No players matched!");
				return;
			}

			if (Ts.Count == 1)
			{
				TSPlayer T = Ts[0];

				if (IPs.ContainsKey(T.IP) && IPs[T.IP].State != VoteState.None)
				{
					e.Player.SendSuccessMessage(string.Format("[TServerWeb] Removed state {0} from IP {1} succesfully.", IPs[T.IP].State, T.IP));
					IPs.Remove(T.IP);
				}
				else
					e.Player.SendSuccessMessage(string.Format("[TServerWeb] No state for IP {0} found.", T.IP));

				return;
			}
			
			string TNames = string.Join(", ", Ts.Select(p => p.Name));
			e.Player.SendSuccessMessage(string.Format("[TServerWeb] Matched multiple players: {0}.", TNames));
		}

		private void CheckVersion(CommandArgs e)
		{
			e.Player.SendInfoMessage(Version.ToString());
		}

		private bool tswQuery(string url, object userToken = null)
		{
			Uri uri = new Uri("http://www.tserverweb.com/vote.php?" + url);

			VoteWC webClient;
			if (this.webClientQueue.TryDequeue(out webClient))
			{
				webClient.DownloadStringAsync(uri, userToken);
				return true;
			}
			return false;
		}

		private void validateCAPTCHA(CommandArgs e)
		{
			if (!IPs.ContainsKey(e.Player.IP)) IPs[e.Player.IP] = new VoteIP(DateTime.Now);
			VoteIP IP = IPs[e.Player.IP];

			if (IP.State != VoteState.Captcha)
			{
				e.Player.SendSuccessMessage("[TServerWeb] We're not awaiting CAPTCHA from you, do /vote first.");
				return;
			}

			int id;
			string message;
			if (!GetServerID(out id, out message))
			{
				Fail("Configuration", message, e.Player, IP);
				return;
			}

			string answer = Uri.EscapeDataString(e.Parameters[0].ToString());
			string playerName = Uri.EscapeDataString(e.Player.Name);

			string url = string.Format("answer={0}&user={1}&sid={2}", answer, playerName, id);

			try
			{
				tswQuery(url, e);
			}
			catch (Exception ex)
			{
				Fail("validateCaptcha", "Attempt to send the query threw an exception: " + ex.Message, e.Player, IP);
			}
		}

		private void doVote(CommandArgs e)
		{
			if (!IPs.ContainsKey(e.Player.IP)) IPs[e.Player.IP] = new VoteIP(DateTime.Now);
			VoteIP IP = IPs[e.Player.IP];

			if (!IP.CanVote())
			{
				IP.NotifyPlayer(e.Player);
				return;
			}

			int id;
			string message;
			if (!GetServerID(out id, out message))
			{
				Fail("Configuration", message, e.Player, IP);
				return;
			}

			IP.State = VoteState.InProgress;
			IP.StateTime = DateTime.Now;
			string url = string.Format("user={0}&sid={1}", Uri.EscapeDataString(e.Player.Name), id);

			try
			{
				tswQuery(url, e);
			}
			catch (Exception ex)
			{
				Fail("doVote", "Attempt to send the query threw an exception: " + ex.Message, e.Player, IP);
			}
		}

		private void Vote(CommandArgs e) // To be fair this should also have a permission.
		{
			try
			{
				if (e.Parameters.Count == 0)
					doVote(e); // Send the vote
				else
					validateCAPTCHA(e); // Answer was provided
			}
			catch (Exception ex)
			{
				Fail("Vote", "Connection failure: " + ex, e.Player);
			}
		}

		private void ChangeID(CommandArgs e)
		{
			if (e.Parameters.Count == 0)
			{
				int id;
				string message;
				if (!GetServerID(out id, out message))
				{
					e.Player.SendErrorMessage("[TServerWeb] Server ID is currently not specified! Please type /tserverweb [number] to set it.");
					return;
				}

				e.Player.SendInfoMessage("[TServerWeb] Server ID is currently set to " + id + ". Type /tserverweb [number] to change it.");
				return;
			}

			if (e.Parameters.Count >= 2)
			{
				e.Player.SendErrorMessage("[TServerWeb] Incorrect syntax! Correct syntax: /tserverweb [number]");
				return;
			}

			int newId;
			if (int.TryParse(e.Parameters[0], out newId))
			{
				TSWConfig.ServerID = newId;
				TSWConfig.Write();

				e.Player.SendInfoMessage("[TServerWeb] Server ID successfully changed to " + newId + "!");
				return;
			}

			e.Player.SendErrorMessage("[TServerWeb] Number not specified! Please type /tserverweb [number]");
		}

		private void SendError(string typeoffailure, string message)
		{
			string Error = string.Format("[TServerWeb] TSWVote Error: {0} failure. Reason: {1}", typeoffailure, message);
			TShock.Log.ConsoleError(Error);
		}

		private bool GetServerID(out int id, out string message)
		{
			id = TSWConfig.ServerID;
			message = String.Empty;

			if (id == 0)
			{
				message = "Server ID not specified. Type /tserverweb [ID] to specify it.";
				return false;
			}

			return true;
		}

		private void WebClient_DownloadStringCompleted(object sender, DownloadStringCompletedEventArgs e)
		{
			VoteWC webClient = sender as VoteWC;

			CommandArgs args = e.UserState as CommandArgs;

			if (args == null)
			{
				ReuseWC(webClient);
				return;
			}

			if (!IPs.ContainsKey(args.Player.IP)) IPs[args.Player.IP] = new VoteIP(DateTime.Now);
			VoteIP IP = IPs[args.Player.IP];

			if (e.Error != null)
			{
				Fail("Exception", e.Error.Message, args.Player, IP);

				ReuseWC(webClient);
				return;
			}

			Response response = Response.Read(e.Result);
			if (response == null)
			{
				Fail("Response", "Invalid response received.", args.Player, IP);

				ReuseWC(webClient);
				return;
			}

			switch (response.response)
			{
				case "success":
					// Correct answer was provided
					// This means a vote is placed
					args.Player.SendSuccessMessage("[TServerWeb] " + response.message);
					if (response.message != "Please wait 24 hours before voting for this server again!")
					{
						IP.State = VoteState.Success;
						IP.StateTime = DateTime.Now;
						VoteHooks.InvokeVoteSuccess(args.Player);
					}
					else
					{
						IP.State = VoteState.Wait;
						IP.StateTime = DateTime.Now;
					}
					break;
				case "failure":
					Fail("Vote", response.message, args.Player, IP);
					break;
				case "captcha":
					args.Player.SendSuccessMessage("[TServerWeb] Please answer the question to make sure you are human.");
					args.Player.SendSuccessMessage("[TServerWeb] You can type /vote <answer>");
					args.Player.SendSuccessMessage("[TServerWeb] (CAPTCHA) " + response.message);
					IP.State = VoteState.Captcha;
					IP.StateTime = DateTime.Now;
					break;
				case "nocaptcha":
					// Answer was provided, but there was no pending captcha
					// Let's consider it a fail, since plugin has VoteStates to prevent this from happening
					Fail("Vote", response.message, args.Player, IP);
					break;
				case "captchafail":
					args.Player.SendErrorMessage("[TServerWeb] Vote failed! Reason: " + response.message);
					IP.State = VoteState.None;
					IP.StateTime = DateTime.Now;
					break;
				case "":
				case null:
				default:
					Fail("Connection", "Response is blank, something is wrong with connection. Please email contact@tserverweb.com about this issue.", args.Player, IP);
					break;
			}

			ReuseWC(webClient);
		}

		private void ReuseWC(VoteWC WC)
		{
			if (WC == null) return;
			webClientQueue.Enqueue(WC);
		}

		internal void Fail(string typeoffailure, string message, TSPlayer Player, VoteIP IP = null)
		{
			SendError(typeoffailure, message);
			if (Player == null || !Player.Active) return;
			Player.SendErrorMessage("[TServerWeb] Vote failed! Please contact an administrator.");
			if (IPs.ContainsKey(Player.IP))
			{
				if (IP == null) IP = IPs[Player.IP];
				IP.Fail();
			}
		}

		private class VoteWC : WebClient
		{
			public static int Timeout
			{
				get
				{
					return TSWConfig == null ? 2000 : TSWConfig.Timeout;
				}
			}

			public VoteWC()
			{
				Proxy = null;
			}

			protected override WebRequest GetWebRequest(Uri uri)
			{
				HttpWebRequest w = base.GetWebRequest(uri) as HttpWebRequest;
				w.Timeout = Timeout;
				w.UserAgent = "TServerWeb Vote Plugin";

				return w;
			}
		}
	}
}
