using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Xml;

using CNode;

using Common.Logging;

using CServices;

namespace SondeTemp 
{
    public class Probe 
	{
        ILogger Logger = Common.Logging.CLogManager.GetLogger("SondeTempCons.Sonde.cs");
        public Probe() {}

		public float Temperature { get; set; }
		public int ID { get; set; }
        public string Code { get; set; }
		public string Name { get; set; }
		public string Description { get; set; }
		public float Min { get; set; }
		public float Max { get; set; }
		public string IPAddress { get; set; }
		public int Port { get; set; }
        public int State { get; set; }
        public int LastState { get; set; }
		private void OnReceive(string address, string msg) 
		{
			string temppMssg = msg.Remove(0, 3);
			temppMssg = temppMssg.Remove(1, 3);
			this.Temperature = float.Parse(temppMssg, System.Globalization.CultureInfo.InvariantCulture.NumberFormat);
		}
        private CSocketLib.CSocket ConfigureSocket() 
		{
            CSocketLib.CSocketConfig socketConfig = new CSocketLib.CSocketConfig(this.IPAddress, this.Port);
            socketConfig.Port = this.Port;
            CSocketLib.CSocket socket = new CSocketLib.CSocket(socketConfig); 
			socket.DataReceived += OnReceive;
			
            return socket;
		}
		public void GetRead() 
		{
			Stopwatch sessionTimer = new Stopwatch();
            CSocketLib.CSocket socket = this.ConfigureSocket();

            try
            {
                socket.Connect();
            } 
			catch (Exception ex) 
			{ 
				Logger.Error(ex); 
			}

			sessionTimer.Restart();
            while (socket == null || socket.State != "Connected" && sessionTimer.ElapsedMilliseconds < 2000) { }
            if (socket.State == "Connected" && this.State != 0) 
			{
				this.State = 1;
                socket.Send("*X01\r\n");
				sessionTimer.Restart();
				while (sessionTimer.ElapsedMilliseconds < 200) { }
				sessionTimer.Stop();
				try 
				{
                    socket.Disconnect(); 
				} 
				catch (Exception ex) 
				{ 
					Logger.Error(ex); 
				}
			}
            else if(this.State != 0)
                this.State = 2;
			sessionTimer.Stop();
		}
    }//end class
}//end namespace
