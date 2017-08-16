#region "REFERENCES"
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.ComponentModel;
using System.Threading;
using CCMailer;
using CNode;
using CServices;
using Common.Logging;
using MaeIoTAPI.Models;
using MaeIoTModel;
using MaeIoTAPI.DAL;
using MaeIoTAPI;
using System.Web.Script.Serialization;
using MaeIoTAPI.Mappers;
using SondeTemp;
using MaeIoTAPI.BL;
#endregion

namespace SondeTempCons 
{
    public class Program
    {
        #region "DECLARATIONS"
        private static bool isAppRunning = false;
        private static float temperatureThreshold;
        private static ILogger logger;
        private static List<Alarm> listAlarms;
        private static List<SondeTemp.Probe> listProbes;
        private static Stopwatch scanStopwatch;
        private static int probeScanIntervalInMs;
        private static int alarmScanIntervalInMs;
        private static CParam param;
        #endregion

        #region
        private void name()
        {
            Console.WriteLine(@"
               _____                 __   ______                   
              / ___/____  ____  ____/ /__/_  __/__  ____ ___  ____ 
              \__ \/ __ \/ __ \/ __  / _ \/ / / _ \/ __ `__ \/ __ \
             ___/ / /_/ / / / / /_/ /  __/ / /  __/ / / / / / /_/ /
            /____/\____/_/ /_/\__,_/\___/_/  \___/_/ /_/ /_/ .___/ v2.0.0
                                                          /_/      
            ");
        }
        #endregion

        #region "MAIN"
        public static void Main(string[] args) 
		{
            
            Program self = new Program();
            logger = Common.Logging.CLogManager.GetLogger("SondeTempCons.Sondes.cs");
            self.Start();
            self.name();
            logger.Info("SondeTemp has started " + DateTime.Now);
            self.ExecuteOperationStack();
            while (isAppRunning) 
			{
                if (scanStopwatch.ElapsedMilliseconds > probeScanIntervalInMs) 
				{
                    self.ExecuteOperationStack();
                }
                self.ScanAlarms();
            }
			logger.Warn("Fin du programme");
        }
        #endregion

        #region "SERVICES"
        public void Start() 
		{
            isAppRunning = true;
            temperatureThreshold = 0;
            param = null;
            scanStopwatch = new Stopwatch();
            listAlarms = new List<Alarm>();
            listProbes = new List<SondeTemp.Probe>();
            try
            {
                param = CParam.GetCParamForCurrentApplication();
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            LoadProbes();
            LoadThresholdConfig();
            LoadIntervalsConfig();
        }
        
        public void Stop() 
		{
            isAppRunning = false;
            scanStopwatch = null;
            listAlarms = null;
            listProbes = null;
            probeScanIntervalInMs = 0;
            alarmScanIntervalInMs = 0;
            temperatureThreshold = 0;
            GarbageCollect();
			logger.Warn("Arrêt du programme");
			Environment.Exit(0);
        }

        private void ExecuteOperationStack()
        {
            ScanProbes();
            EvaluateReadings();
            ScanAlarms();
            scanStopwatch.Restart();
        }
        #endregion
        
        #region "LOADINGS"

        public void LoadProbes()
		{
            List<MaeIoTAPI.Models.Node> nodeList = new List<MaeIoTAPI.Models.Node>();
            nodeList = GetProbe();
            foreach (Node node in nodeList)
            {
                SondeTemp.Probe probe = new SondeTemp.Probe
                {
                    ID = (int)node.IDNode,
                    Name = node.Nom,
                    Description = node.Description,
                    Code = node.Code,
                    Min = Convert.ToSingle(node.AlarmeMin),
                    Max = Convert.ToSingle(node.AlarmeMax),
                    IPAddress = node.IPAddress,
                    Port = node.Port,
                    State = node.State,
                    LastState = node.State
                };
                listProbes.Add(probe);
            }
        }

        public void LoadThresholdConfig()
        {
            if (param != null)
            {
                XmlNode xNodesThreshold = param.GetParams("Param/Threshold");
                temperatureThreshold = Math.Abs(float.Parse(xNodesThreshold.Attributes["Value"].Value, CultureInfo.InvariantCulture.NumberFormat));
            }
            if (temperatureThreshold < 0 || param == null)
            {
                temperatureThreshold = 0.5f;
                logger.Error("Threshold for min/max values is missing or incorrect. Default min/max threshold applied: ±0.5°C");
            }
        }

		public void LoadIntervalsConfig()
		{
            if (param != null)
            {
                XmlNode xNodesInterval = param.GetParams("Param/Intervals");
                foreach (XmlNode node in xNodesInterval)
                {
                    //Convert minutes to milliseconds before insertion
                    if (node.Attributes["Name"].Value == "ProbesInterval")
                        probeScanIntervalInMs = MinutesToMilliseconds(node.Attributes["Minutes"].Value);
                    else if (node.Attributes["Name"].Value == "AlarmsInterval")
                        alarmScanIntervalInMs = MinutesToMilliseconds(node.Attributes["Minutes"].Value);
                }
            } 
            if (alarmScanIntervalInMs <= 0 || param == null)
            {
                alarmScanIntervalInMs = 1800000;
                logger.Error("Interval for alarms is missing or incorrect. Default alarm scan interval applied (30 min./1 800 000ms)");
            }
            if (probeScanIntervalInMs <= 0 || param == null)
            {
                probeScanIntervalInMs = 1800000;
                logger.Error("Interval for probes is missing or incorrect. Default probe scan interval applied (30 min./1 800 000ms)");
            }
		}

        public Int32 MinutesToMilliseconds(string minutes)
        {
            return Convert.ToInt32(minutes) * 60000;
        }
        #endregion

        #region "SCANS"
        public void ScanProbes() 
		{
            logger.Info("Scanning probes...");
            foreach (SondeTemp.Probe sonde in listProbes)
            {
                sonde.GetRead();
                if (sonde.LastState != sonde.State && sonde.State != 0)
                {
                    UpdateProbeState(sonde.ID, sonde.State);
                    sonde.LastState = sonde.State;
                }
                if(sonde.State == 1)
                    PostProbeData(sonde.ID, sonde.Temperature);
            }           
        }

        private void ScanAlarms()
        {
            foreach (Alarm alarm in listAlarms)
            {
                if (alarm.Timestamp.AddMilliseconds(alarmScanIntervalInMs) < DateTime.Now)
                {
                    listAlarms.Where(x => x.ProbeID == alarm.ProbeID).SingleOrDefault().resetTimestamp();
                    Task.Run(() => SendAlarmEmail(alarm.ProbeID, alarm.Type));
                }
            }
        }
        #endregion

        #region "ALARMS"
        private void EvaluateReadings()
        {
            List<MaeIoTAPI.Models.Client> clients = new List<MaeIoTAPI.Models.Client>(); 
            clients = GetClient();
            float lecture = 0;
            List<MaeIoTAPI.Models.Client> clientLastReads = new List<MaeIoTAPI.Models.Client>();

            // Get the last reading of each probe for each clients
            foreach (MaeIoTAPI.Models.Client client in clients)
            {
                clientLastReads.Add(GetClientLastReads(client.IDClient));
            }

            // Iterating through each clients
            foreach (MaeIoTAPI.Models.Client client in clientLastReads)
            {
                // Iteratiing through each probes
                foreach (MaeIoTAPI.Models.Node node in client.Nodes)
                {
                    // Iterating through each readings
                    foreach(MaeIoTAPI.Models.NodeData probeData in node.NodeDatas)
                    {
                        if (node.State == 0)
                            logger.Warn(string.Format("{0}:{1} {2} : inactive", node.IPAddress, node.Port, node.Code));
                        else if (node.State == 1)
                        {
                            lecture = Convert.ToSingle(probeData.Value);
                            logger.Info(string.Format("{0}:{1} {2} : {3}°C", node.IPAddress, node.Port, node.Code, lecture));
                            if (lecture < node.AlarmeMin - temperatureThreshold || lecture > node.AlarmeMax + temperatureThreshold)
                            {
                                logger.Warn(string.Format("** La temperature de {0} est anormale (min: {1}°C max: {2}°C tolérance: ±{3}°C) **", node.Code, node.AlarmeMin, node.AlarmeMax, temperatureThreshold));
                                AddAlarmToList(node, lecture, 1);
                            }
                            else
                                RemoveAlarmFromList(node.IDNode);
                        }
                        else if (node.State == 2)
                        {
                            logger.Warn(string.Format("{0}:{1} {2} : hors-ligne", node.IPAddress, node.Port, node.Code));
                            AddAlarmToList(node, lecture, 0);
                            RemoveAlarmFromList(node.IDNode);
                        }
                    }
                }
            }
        }

        private void SendAlarmEmail(int probeID, int typeAlarm)
        {
            XmlNode xNodeEmails = param.GetParams("Param/Emails");

            string alertType;
            switch (typeAlarm)
            {
                case 0:
                    alertType = "Connection";
                    break;
                case 1:
                    alertType = "Temperature";
                    break;
                default:
                    alertType = "N/A";
                    break;
            }
            var probe = listProbes.Where(x => x.ID == probeID).SingleOrDefault();
            string subject = xNodeEmails.SelectSingleNode(alertType).Attributes["Subject"].Value;
            string body = string.Format(xNodeEmails.SelectSingleNode(alertType).Attributes["Body"].Value, probe.ID.ToString(), probe.Description, probe.Min, probe.Max, probe.Temperature);

            SendEmail(subject, body, GetProbeEmails(probeID));
        }

        private bool AlarmExists(int probeID)
        {
            return listAlarms.Any(x => x.ProbeID == probeID);
        }

        /// <param name="type">0: Connexion
        ///                    1: Temperature</param>
        private void AddAlarmToList(Node probe, float lecture, int typeAlarm)
        {
            if (AlarmExists(probe.IDNode) == false)
            {
                Alarm alarm = new Alarm 
                {
                    ProbeID = probe.IDNode, 
                    Read = lecture, 
                    Min = Convert.ToSingle(probe.AlarmeMin), 
                    Max = Convert.ToSingle(probe.AlarmeMax), 
                    Type = typeAlarm 
                };
                listAlarms.Add(alarm);
                Task.Run(() => SendAlarmEmail(probe.IDNode, typeAlarm));
            }
        }
        
        private void RemoveAlarmFromList(int alarmID)
        {
            if (AlarmExists(alarmID) == true)
                listAlarms.RemoveAll(x => (x.ProbeID == alarmID));
        }

        private void SendEmail(string subject, string body, List<MaeIoTAPI.Models.NodeEmail> recipients)
        {
            CCMailer.CSmtpMailerProperty client = new CCMailer.CSmtpMailerProperty();

            XmlNode xNodeSMTP = param.GetParams("Param/SMTP");
            client.mServer = xNodeSMTP.SelectSingleNode("Server").Attributes["Host"].Value;
            client.mPort = xNodeSMTP.SelectSingleNode("Server").Attributes["Port"].Value;
            client.mSubject = subject;
            client.mBody = body;
            client.mUser = xNodeSMTP.SelectSingleNode("Credential").Attributes["User"].Value;
            client.mPass = xNodeSMTP.SelectSingleNode("Credential").Attributes["Pass"].Value;
            client.mSignature = xNodeSMTP.SelectSingleNode("Signature").InnerText;
            client.mMailPriority = System.Net.Mail.MailPriority.Normal;
            client.mUseSSL = true;

            foreach (NodeEmail recipient in recipients)
            {
                client.mDestAddr = recipient.Email;
                try
                {
                    CCMailer.CSmtpMailer mailer = new CCMailer.CSmtpMailer(client);
                    mailer.Send();
                    logger.Info("Email sent to " + recipient.Email);
                }
                catch (Exception ex)
                {
                    logger.Error("Error sending email to " + recipient.Email + ": " + ex);
                }
            }
        }

        #endregion

        #region "STATS"

        private double Moyenne(List<NodeData> ProbeData)
        {
            double sum = 0;
            for (int i = 0; i < ProbeData.Count; i++)
            {
                sum = sum + ProbeData[i].Value;
            }
            return (sum / ProbeData.Count);
        }

        private double Min(List<NodeData> ProbeData)
        {
            double min = ProbeData[0].Value;
            for (int i = 0; i < ProbeData.Count; i++)
            {
                if (ProbeData[i].Value < min)
                {
                    min = ProbeData[i].Value;
                }
            }
            return min;
        }

        private double Max(List<NodeData> ProbeData)
        {
            double max = ProbeData[0].Value;
            for (int i = 0; i < ProbeData.Count; i++)
            {
                if (ProbeData[i].Value > max)
                {
                    max = ProbeData[i].Value;
                }
            }
            return max;
        }

        private void stats(MaeIoTAPI.Models.Client client)
        {
            int idSonde = 1; 
            foreach(Node probe in client.Nodes)
            {
                if (probe.IDNode == idSonde)
                {
                    double min = Min(probe.NodeDatas.ToList());
                    double max = Max(probe.NodeDatas.ToList());
                    double moy = Moyenne(probe.NodeDatas.ToList());
                }
            }
        }

        #endregion
        
        #region "CRUD"
        // Some CRUD functions return empty lists so the app can run indefinitely instead of crashing since it runs on a server without any interaction
        public List<MaeIoTAPI.Models.Client> GetClient()
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            string response = QueryAPI("Client");
            if (response != "")
                return jsSerializer.Deserialize<List<MaeIoTAPI.Models.Client>>(response);
            else
                return Enumerable.Empty<MaeIoTAPI.Models.Client>().ToList<MaeIoTAPI.Models.Client>();
        }

        public MaeIoTAPI.Models.Client GetClient(int clientID)
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            return jsSerializer.Deserialize<MaeIoTAPI.Models.Client>(QueryAPI(string.Format("{0}/{1}", "client", clientID)));
        }

        public MaeIoTAPI.Models.Client GetClient(int pIDClient, DateTime fromDate, DateTime toDate)
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            return jsSerializer.Deserialize<MaeIoTAPI.Models.Client>(QueryAPI(string.Format("client?IDClient={0}&fromDate={1}&toDate={2}", pIDClient, fromDate, toDate)));
        }

        public List<MaeIoTAPI.Models.NodeEmail> GetProbeEmails(int probeID)
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            List<MaeIoTAPI.Models.NodeEmail> recipients = new List<MaeIoTAPI.Models.NodeEmail>();
            string response = QueryAPI(string.Format("node?pID={0}&Last=2", probeID));
            if (response != "") 
            {
                MaeIoTAPI.Models.Node serializedResponse = jsSerializer.Deserialize<MaeIoTAPI.Models.Node>(response);
                foreach (NodeEmail email in serializedResponse.NodeEmails)
                {
                    recipients.Add(email);
                }
            }
            return recipients;
        }

        public MaeIoTAPI.Models.Client GetClientLastReads(int clientID)
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            return jsSerializer.Deserialize<MaeIoTAPI.Models.Client>(QueryAPI(string.Format("client?pId={0}&Last=1", clientID)));
        }

        public List<MaeIoTAPI.Models.Node> GetProbe()
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            string response = QueryAPI("node");
            if (response != "")
                return jsSerializer.Deserialize<List<MaeIoTAPI.Models.Node>>(response);
            else
                return Enumerable.Empty<MaeIoTAPI.Models.Node>().ToList<MaeIoTAPI.Models.Node>();
        }

        public MaeIoTAPI.Models.Node GetProbe(int probeID)
        {
            JavaScriptSerializer jsSerializer = new JavaScriptSerializer();
            return jsSerializer.Deserialize<MaeIoTAPI.Models.Node>(QueryAPI(string.Format("{0}/{1}", "node", probeID)));
        }

        private void PostProbeData(int probeID, float temperature)
        {
            NodeData nd = new NodeData { IDNode = Convert.ToInt16(probeID), TimeRead = DateTime.Now, Value = Convert.ToDouble(temperature) };
            HttpClient client = InitHttp();

            try
            {
                using (client)
                {
                    HttpResponseMessage response = client.PostAsJsonAsync(string.Format("{0}/{1}", "device", nd.IDNodeData), nd).Result;
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex.ToString());
            }
        }

        private async Task PostProbe(SondeTemp.Probe probe)
        {
            Node nd = MapNode(probe);
            HttpClient client = InitHttp();

            HttpResponseMessage response = await client.PostAsJsonAsync(string.Format("{0}/{1}", "device", nd.IDNode), nd);
            response.EnsureSuccessStatusCode();
        }

        private async void UpdateProbeState(int probeID, int probeState)
        {
            MaeIoTAPI.Models.Node probe = GetProbe(probeID);
            HttpClient client = InitHttp();

            if (probe == null)
                return;

            probe.State = probeState;
            HttpResponseMessage response =  await client.PutAsJsonAsync(string.Format("{0}/{1}", "node", probeID), probe);
            response.EnsureSuccessStatusCode();
        }

        public string QueryAPI(string apiPath)
        {
            HttpClient client = InitHttp();

            try
            {
                using (client)
                {
                    var response = client.GetAsync(apiPath).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = response.Content;
                        return response.Content.ReadAsStringAsync().Result;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex);
            }
            return "";
        }

        private Node MapNode(SondeTemp.Probe probe)
        {
            return new Node
            {
                IDNode = Convert.ToInt16(probe.ID),
                IDNodeDef = Convert.ToInt16("1"),
                Code = probe.Name,
                Nom = probe.Description,
                Description = probe.Description + " " + probe.Code,
                AlarmeMin = Convert.ToDouble(probe.Min),
                AlarmeMax = Convert.ToDouble(probe.Max),
                IPAddress = probe.IPAddress,
                Port = probe.Port,
                State = probe.State
            };
        }

        private HttpClient InitHttp()
        {
            HttpClient client = new HttpClient();
            if (client != null)
            {
                client.Dispose();
                client = null;
            }
			
            client = new HttpClient() { BaseAddress = new Uri("http://127.0.0.1") };
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            
            return client;
        }

        #endregion

        #region "MISC"
        private void GarbageCollect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        #endregion

    }//end class
}//end namespace