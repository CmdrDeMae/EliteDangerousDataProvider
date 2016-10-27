﻿using EddiCompanionAppService;
using EddiDataDefinitions;
using EddiDataProviderService;
using EddiEvents;
using EddiSpeechService;
using EddiStarMapService;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Utilities;

namespace Eddi
{
    /// <summary>
    /// Eddi is the controller for all EDDI operations.  Its job is to retain the state of the objects such as the commander, the current system, etc.
    /// and keep them up-to-date with changes that occur.  It also passes on messages to responders to handle as required.
    /// </summary>
    public class EDDI
    {
        private static EDDI instance;

        private static bool started;

        static EDDI()
        {
            // Set up our app directory
            Directory.CreateDirectory(Constants.DATA_DIR);

            // Use invariant culture to ensure that we use . rather than , for our separator when writing out decimals
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }

        private static readonly object instanceLock = new object();
        public static EDDI Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (instanceLock)
                    {
                        if (instance == null)
                        {
                            Logging.Debug("No EDDI instance: creating one");
                            instance = new EDDI();
                        }
                    }
                }
                return instance;
            }
        }

        public bool avoidPhonetics = false;

        public List<EDDIMonitor> monitors = new List<EDDIMonitor>();
        // Each monitor runs in its own thread
        private List<Thread> monitorThreads = new List<Thread>();

        public List<EDDIResponder> responders = new List<EDDIResponder>();
        private List<EDDIResponder> activeResponders = new List<EDDIResponder>();

        // Information obtained from the companion app service
        public Commander Cmdr { get; private set; }
        public Ship Ship { get; private set; }
        public List<Ship> Shipyard { get; private set; }
        public Station LastStation { get; private set; }

        // Services made available from EDDI
        public StarMapService starMapService { get; private set; }

        // Information obtained from the configuration
        public StarSystem HomeStarSystem { get; private set; }
        public Station HomeStation { get; private set; }

        // Information obtained from the log watcher
        public string Environment { get; private set; }
        public StarSystem CurrentStarSystem { get; private set; }
        public StarSystem LastStarSystem { get; private set; }

        private EDDI()
        {
            try
            {
                Logging.Info(Constants.EDDI_NAME + " " + Constants.EDDI_VERSION + " starting");

                // Start by ensuring that our primary data structures have something in them.  This allows them to be updated
                // from any source
                Cmdr = new Commander();
                Ship = new Ship();
                Shipyard = new List<Ship>();

                // Set up the EDDI configuration
                EDDIConfiguration configuration = EDDIConfiguration.FromFile();
                Logging.Verbose = configuration.Debug;
                avoidPhonetics = configuration.AvoidPhonetic;
                if (configuration.HomeSystem != null && configuration.HomeSystem.Trim().Length > 0)
                {
                    HomeStarSystem = StarSystemSqLiteRepository.Instance.GetOrCreateStarSystem(configuration.HomeSystem.Trim());
                    if (HomeStarSystem != null)
                    {
                        Logging.Debug("Home star system is " + HomeStarSystem.name);
                        if (configuration.HomeStation != null && configuration.HomeStation.Trim().Length > 0)
                        {
                            string homeStationName = configuration.HomeStation.Trim();
                            foreach (Station station in HomeStarSystem.stations)
                            {
                                if (station.name == homeStationName)
                                {
                                    HomeStation = station;
                                    Logging.Debug("Home station is " + HomeStation.name);
                                    break;

                                }
                            }
                        }
                    }
                }

                // Set up the app service
                if (CompanionAppService.Instance.CurrentState == CompanionAppService.State.READY)
                {
                    // Carry out initial population of profile
                    try
                    {
                        refreshProfile();
                    }
                    catch (Exception ex)
                    {
                        Logging.Debug("Failed to obtain profile: " + ex);
                    }
                }

                Cmdr.insurance = configuration.Insurance;
                if (Cmdr.name != null)
                {
                    Logging.Info("EDDI access to the companion app is enabled");
                }
                else
                {
                    // If InvokeUpdatePlugin failed then it will have have left an error message, but this once we ignore it
                    Logging.Info("EDDI access to the companion app is disabled");
                }

                // Set up the star map service
                StarMapConfiguration starMapCredentials = StarMapConfiguration.FromFile();
                if (starMapCredentials != null && starMapCredentials.apiKey != null)
                {
                    // Commander name might come from star map credentials or the companion app's profile
                    string commanderName = null;
                    if (starMapCredentials.commanderName != null)
                    {
                        commanderName = starMapCredentials.commanderName;
                    }
                    else if (Cmdr != null && Cmdr.name != null)
                    {
                        commanderName = Cmdr.name;
                    }
                    if (commanderName != null)
                    {
                        starMapService = new StarMapService(starMapCredentials.apiKey, commanderName);
                        Logging.Info("EDDI access to EDSM is enabled");
                    }
                }
                if (starMapService == null)
                {
                    Logging.Info("EDDI access to EDSM is disabled");
                }

                // We always start in normal space
                Environment = Constants.ENVIRONMENT_NORMAL_SPACE;

                // Set up monitors and responders
                monitors = findMonitors();
                responders = findResponders();

                // Check for an update
                string response;
                try
                {
                    if (Constants.EDDI_VERSION.Contains("b"))
                    {
                        response = Net.DownloadString("http://api.eddp.co/betaversion");
                    }
                    else
                    {
                        response = Net.DownloadString("http://api.eddp.co/version");
                    }
                    if (Versioning.Compare(response, Constants.EDDI_VERSION) == 1)
                    {
                        SpeechService.Instance.Say(null, "EDDI version " + response.Replace(".", " point ") + " is now available.", false);
                    }
                }
                catch
                {
                    SpeechService.Instance.Say(null, "There was a problem connecting to external data services; some features may not work fully", false);
                }

                Logging.Info(Constants.EDDI_NAME + " " + Constants.EDDI_VERSION + " initialised");
            }
            catch (Exception ex)
            {
                Logging.Error("Failed to initialise: " + ex.ToString());
            }
        }

        public void Start()
        {
            if (!started)
            {
                EDDIConfiguration configuration = EDDIConfiguration.FromFile();

                foreach (EDDIMonitor monitor in monitors)
                {
                    bool enabled;
                    if (!configuration.Plugins.TryGetValue(monitor.MonitorName(), out enabled))
                    {
                        // No information; default to enabled
                        enabled = true;
                    }

                    if (!enabled)
                    {
                        Logging.Debug(monitor.MonitorName() + " is disabled; not starting");
                    }
                    else
                    {
                        Thread monitorThread = new Thread(() => monitor.Start());
                        monitorThread.Name = monitor.MonitorName();
                        monitorThread.IsBackground = true;
                        Logging.Info("Starting " + monitor.MonitorName());
                        monitorThread.Start();
                    }
                }

                foreach (EDDIResponder responder in responders)
                {
                    bool enabled;
                    if (!configuration.Plugins.TryGetValue(responder.ResponderName(), out enabled))
                    {
                        // No information; default to enabled
                        enabled = true;
                    }

                    if (!enabled)
                    {
                        Logging.Debug(responder.ResponderName() + " is disabled; not starting");
                    }
                    else
                    {
                        bool responderStarted = responder.Start();
                        if (responderStarted)
                        {
                            activeResponders.Add(responder);
                            //EventHandler += new OnEventHandler(responder.Handle);
                            Logging.Info("Started " + responder.ResponderName());
                        }
                        else
                        {
                            Logging.Warn("Failed to start " + responder.ResponderName());
                        }
                    }
                }
                started = true;
            }
        }

        public void Stop()
        {
            if (started)
            {
                foreach (EDDIResponder responder in responders)
                {
                    responder.Stop();
                    activeResponders.Remove(responder);
                }
                foreach (EDDIMonitor monitor in monitors)
                {
                    monitor.Stop();
                }
            }

            Logging.Info(Constants.EDDI_NAME + " " + Constants.EDDI_VERSION + " stopped");

            started = false;
        }

        /// <summary>
        /// Reload all monitors and responders
        /// </summary>
        public void Reload()
        {
            foreach (EDDIResponder responder in responders)
            {
                responder.Reload();
            }
            foreach (EDDIMonitor monitor in monitors)
            {
                monitor.Reload();
            }

            Logging.Info(Constants.EDDI_NAME + " " + Constants.EDDI_VERSION + " stopped");
        }

        /// <summary>
        /// Reload a specific monitor or responder
        /// </summary>
        public void Reload(string name)
        {
            foreach (EDDIResponder responder in responders)
            {
                if (responder.ResponderName() == name)
                {
                    responder.Reload();
                    return;
                }
            }
            foreach (EDDIMonitor monitor in monitors)
            {
                if (monitor.MonitorName() == name)
                {
                    monitor.Reload();
                }
            }

            Logging.Info(Constants.EDDI_NAME + " " + Constants.EDDI_VERSION + " stopped");
        }

        public void eventHandler(Event journalEvent)
        {
            Logging.Debug("Handling event " + JsonConvert.SerializeObject(journalEvent));
            // We have some additional processing to do for a number of events
            bool passEvent = true;
            if (journalEvent is JumpingEvent)
            {
                passEvent = eventJumping((JumpingEvent)journalEvent);
            }
            else if (journalEvent is JumpedEvent)
            {
                passEvent = eventJumped((JumpedEvent)journalEvent);
            }
            else if (journalEvent is DockedEvent)
            {
                passEvent = eventDocked((DockedEvent)journalEvent);
            }
            else if (journalEvent is UndockedEvent)
            {
                passEvent = eventUndocked((UndockedEvent)journalEvent);
            }
            else if (journalEvent is EnteredSupercruiseEvent)
            {
                passEvent = eventEnteredSupercruise((EnteredSupercruiseEvent)journalEvent);
            }
            else if (journalEvent is EnteredNormalSpaceEvent)
            {
                passEvent = eventEnteredNormalSpace((EnteredNormalSpaceEvent)journalEvent);
            }
            else if (journalEvent is ShipDeliveredEvent)
            {
                passEvent = eventShipDeliveredEvent((ShipDeliveredEvent)journalEvent);
            }
            else if (journalEvent is ShipSwappedEvent)
            {
                passEvent = eventShipSwappedEvent((ShipSwappedEvent)journalEvent);
            }
            else if (journalEvent is ShipSoldEvent)
            {
                passEvent = eventShipSoldEvent((ShipSoldEvent)journalEvent);
            }
            else if (journalEvent is CommanderContinuedEvent)
            {
                passEvent = eventCommanderContinuedEvent((CommanderContinuedEvent)journalEvent);
            }
            // Additional processing is over, send to the event responders if required
            if (passEvent)
            {
                OnEvent(journalEvent);
            }
        }

        private void OnEvent(Event @event)
        {
            foreach (EDDIResponder responder in activeResponders)
            {
                Thread responderThread = new Thread(() => responder.Handle(@event));
                responderThread.Name = responder.ResponderName();
                responderThread.IsBackground = true;
                responderThread.Start();
            }
        }

        private bool eventDocked(DockedEvent theEvent)
        {
            updateCurrentSystem(theEvent.system);

            // Update the station
            Station station = CurrentStarSystem.stations.Find(s => s.name == theEvent.station);
            if (station == null)
            {
                // This station is unknown to us, might not be in EDDB or we might not have connectivity.  Use a placeholder
                station = new Station();
                station.name = theEvent.station;
                station.systemname = theEvent.system;
            }

            // Information from the event might be more current than that from EDDB so use it in preference
            station.state = theEvent.factionstate;
            station.faction = theEvent.faction;
            station.government = theEvent.government;
            station.allegiance = theEvent.allegiance;

            LastStation = station;

            // Now call refreshProfile() to obtain the outfitting and commodity information
            refreshProfile();

            return true;
        }

        private bool eventUndocked(UndockedEvent theEvent)
        {
            return true;
        }

        private bool eventLocation(LocationEvent theEvent)
        {
            updateCurrentSystem(theEvent.system);
            // Always update the current system with the current co-ordinates, just in case things have changed
            CurrentStarSystem.x = theEvent.x;
            CurrentStarSystem.y = theEvent.y;
            CurrentStarSystem.z = theEvent.z;
            return true;
        }

        private void updateCurrentSystem(string name)
        {
            if (name == null)
            {
                return;
            }
            if (CurrentStarSystem == null || CurrentStarSystem.name != name)
            {
                LastStarSystem = CurrentStarSystem;
                CurrentStarSystem = StarSystemSqLiteRepository.Instance.GetOrCreateStarSystem(name);
                setSystemDistanceFromHome(CurrentStarSystem);
            }
        }

        private bool eventJumping(JumpingEvent theEvent)
        {
            bool passEvent;
            Logging.Debug("Jumping to " + theEvent.system);
            if (CurrentStarSystem == null || CurrentStarSystem.name != theEvent.system)
            {
                // New system
                passEvent = true;
                updateCurrentSystem(theEvent.system);
                // The information in the event is more up-to-date than the information we obtain from external sources, so update it here
                CurrentStarSystem.x = theEvent.x;
                CurrentStarSystem.y = theEvent.y;
                CurrentStarSystem.z = theEvent.z;
                CurrentStarSystem.visits++;
                CurrentStarSystem.lastvisit = DateTime.Now;
                StarSystemSqLiteRepository.Instance.SaveStarSystem(CurrentStarSystem);
                setCommanderTitle();
            }
            else
            {
                // Restatement of current system
                passEvent = false;
            }

            // Whilst jumping we are in witch space
            Environment = Constants.ENVIRONMENT_WITCH_SPACE;

            return passEvent;
        }

        private bool eventJumped(JumpedEvent theEvent)
        {
            bool passEvent;
            Logging.Debug("Jumped to " + theEvent.system);
            if (CurrentStarSystem == null || CurrentStarSystem.name != theEvent.system)
            {
                // New system
                passEvent = true;
                updateCurrentSystem(theEvent.system);
                // The information in the event is more up-to-date than the information we obtain from external sources, so update it here
                CurrentStarSystem.x = theEvent.x;
                CurrentStarSystem.y = theEvent.y;
                CurrentStarSystem.z = theEvent.z;
                CurrentStarSystem.allegiance = theEvent.allegiance;
                CurrentStarSystem.faction = theEvent.faction;
                CurrentStarSystem.primaryeconomy = theEvent.economy;
                CurrentStarSystem.government = theEvent.government;
                CurrentStarSystem.security = theEvent.security;

                CurrentStarSystem.visits++;
                CurrentStarSystem.lastvisit = DateTime.Now;
                StarSystemSqLiteRepository.Instance.SaveStarSystem(CurrentStarSystem);
                setCommanderTitle();
            }
            else if (CurrentStarSystem.name == theEvent.system && Environment == Constants.ENVIRONMENT_SUPERCRUISE)
            {
                // Restatement of current system
                passEvent = false;
            }
            else if (CurrentStarSystem.name == theEvent.system && Environment == Constants.ENVIRONMENT_WITCH_SPACE)
            {
                passEvent = true;

                // Jumped event following a Jumping event, so most information is up-to-date but we should pass this anyway for
                // plugin triggers

                // The information in the event is more up-to-date than the information we obtain from external sources, so update it here
                CurrentStarSystem.allegiance = theEvent.allegiance;
                CurrentStarSystem.faction = theEvent.faction;
                CurrentStarSystem.primaryeconomy = theEvent.economy;
                CurrentStarSystem.government = theEvent.government;
                CurrentStarSystem.security = theEvent.security;
                setCommanderTitle();
            }
            else
            {
                passEvent = true;
                updateCurrentSystem(theEvent.system);

                // The information in the event is more up-to-date than the information we obtain from external sources, so update it here
                CurrentStarSystem.x = theEvent.x;
                CurrentStarSystem.y = theEvent.y;
                CurrentStarSystem.z = theEvent.z;
                CurrentStarSystem.allegiance = theEvent.allegiance;
                CurrentStarSystem.faction = theEvent.faction;
                CurrentStarSystem.primaryeconomy = theEvent.economy;
                CurrentStarSystem.government = theEvent.government;
                CurrentStarSystem.security = theEvent.security;

                CurrentStarSystem.visits++;
                CurrentStarSystem.lastvisit = DateTime.Now;
                StarSystemSqLiteRepository.Instance.SaveStarSystem(CurrentStarSystem);
                setCommanderTitle();
            }

            // After jump has completed we are always in supercruise
            Environment = Constants.ENVIRONMENT_SUPERCRUISE;

            return passEvent;
        }

        private bool eventEnteredSupercruise(EnteredSupercruiseEvent theEvent)
        {
            if (Environment == null || Environment != Constants.ENVIRONMENT_SUPERCRUISE)
            {
                Environment = Constants.ENVIRONMENT_SUPERCRUISE;
                updateCurrentSystem(theEvent.system);
                return true;
            }
            return false;
        }

        private bool eventEnteredNormalSpace(EnteredNormalSpaceEvent theEvent)
        {
            if (Environment == null || Environment != Constants.ENVIRONMENT_NORMAL_SPACE)
            {
                Environment = Constants.ENVIRONMENT_NORMAL_SPACE;
                updateCurrentSystem(theEvent.system);
                return true;
            }
            return false;
        }

        private bool eventShipDeliveredEvent(ShipDeliveredEvent theEvent)
        {
            refreshProfile();
            SetShip(theEvent.Ship);
            return true;
        }

        private bool eventShipSwappedEvent(ShipSwappedEvent theEvent)
        {
            SetShip(theEvent.Ship);
            return true;
        }

        private bool eventShipSoldEvent(ShipSoldEvent theEvent)
        {
            // Need to update shipyard
            refreshProfile();
            return true;
        }

        private bool eventCommanderContinuedEvent(CommanderContinuedEvent theEvent)
        {
            SetShip(theEvent.Ship);

            if (Cmdr.name == null)
            {
                Cmdr.name = theEvent.commander;
            }

            return true;
        }

        /// <summary>Obtain information from the companion API and use it to refresh our own data</summary>
        public void refreshProfile()
        {
            if (CompanionAppService.Instance != null && CompanionAppService.Instance.CurrentState == CompanionAppService.State.READY)
            {
                try
                {
                    Profile profile = CompanionAppService.Instance.Profile();
                    if (profile != null)
                    {
                        // Use the profile as primary information for our commander and shipyard
                        Cmdr = profile.Cmdr;
                        Shipyard = profile.Shipyard;

                        // Only use the ship information if we agree that this is the correct ship to use
                        if (Ship.model == null || profile.Ship.LocalId == Ship.LocalId)
                        {
                            SetShip(profile.Ship);
                        }

                        // Only set the current star system if it is not present, otherwise we leave it to events
                        if (CurrentStarSystem == null)
                        {
                            CurrentStarSystem = profile == null ? null : profile.CurrentStarSystem;
                            setSystemDistanceFromHome(CurrentStarSystem);
                        }

                        if (LastStation == null)
                        {
                            Logging.Info("No last station; using the information available to us from the profile");
                        }
                        else
                        {
                            Logging.Info("Internal last station is " + LastStation.name + "@" + LastStation.systemname + ", profile last station is " + LastStation.name + "@" + LastStation.systemname);
                        }

                        // Last station's name should be set from the journal, so we confirm that this is correct
                        // before we update the commodity and outfitting information
                        if (LastStation == null)
                        {
                            // No current info so use profile data directly
                            LastStation = profile.LastStation;
                        }
                        else if (LastStation.systemname == profile.LastStation.systemname && LastStation.name == profile.LastStation.name)
                        {
                            // Match for our expected station with the information returned from the profile

                            // Update the outfitting, commodities and shipyard with the data obtained from the profile
                            LastStation.outfitting = profile.LastStation.outfitting;
                            LastStation.commodities = profile.LastStation.commodities;
                            LastStation.shipyard = profile.LastStation.shipyard;
                        }

                        setCommanderTitle();
                    }
                }
                catch (Exception ex)
                {
                    Logging.Error("Exception obtaining profile: " + ex.ToString());
                }
            }
        }

        private void SetShip(Ship ship)
        {
            if (ship == null)
            {
                Logging.Warn("Refusing to set ship to null");
            }
            else
            {
                Logging.Debug("Set ship to " + JsonConvert.SerializeObject(ship));
                Ship = ship;
            }
        }

        private void setSystemDistanceFromHome(StarSystem system)
        {
            Logging.Info("HomeStarSystem is " + (HomeStarSystem == null ? null : HomeStarSystem.name));
            if (HomeStarSystem != null && HomeStarSystem.x != null && system.x != null)
            {
                system.distancefromhome = (decimal)Math.Round(Math.Sqrt(Math.Pow((double)(system.x - HomeStarSystem.x), 2)
                                                                      + Math.Pow((double)(system.y - HomeStarSystem.y), 2)
                                                                      + Math.Pow((double)(system.z - HomeStarSystem.z), 2)), 2);
                Logging.Info("Distance from home is " + system.distancefromhome);
            }
        }

        /// <summary>Work out the title for the commander in the current system</summary>
        private static int minEmpireRankForTitle = 3;
        private static int minFederationRankForTitle = 1;
        private void setCommanderTitle()
        {
            if (Cmdr != null)
            {
                Cmdr.title = "Commander";
                if (CurrentStarSystem != null)
                {
                    if (CurrentStarSystem.allegiance == "Federation" && Cmdr.federationrating != null && Cmdr.federationrating.rank > minFederationRankForTitle)
                    {
                        Cmdr.title = Cmdr.federationrating.name;
                    }
                    else if (CurrentStarSystem.allegiance == "Empire" && Cmdr.empirerating != null && Cmdr.empirerating.rank > minEmpireRankForTitle)
                    {
                        Cmdr.title = Cmdr.empirerating.name;
                    }
                }
            }
        }

        /// <summary>
        /// Find all monitors
        /// </summary>
        private List<EDDIMonitor> findMonitors()
        {
            DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            List<EDDIMonitor> monitors = new List<EDDIMonitor>();
            Type pluginType = typeof(EDDIMonitor);
            foreach (FileInfo file in dir.GetFiles("*Monitor.dll", SearchOption.AllDirectories))
            {
                Logging.Debug("Checking potential plugin at " + file.FullName);
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file.FullName);
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type.IsInterface || type.IsAbstract)
                        {
                            continue;
                        }
                        else
                        {
                            if (type.GetInterface(pluginType.FullName) != null)
                            {
                                Logging.Debug("Instantiating monitor plugin at " + file.FullName);
                                EDDIMonitor monitor = type.InvokeMember(null,
                                                           BindingFlags.CreateInstance,
                                                           null, null, null) as EDDIMonitor;
                                monitors.Add(monitor);
                            }
                        }
                    }
                }
                catch (BadImageFormatException)
                {
                    // Ignore this; probably due to CPU architecture mismatch
                }
                catch (ReflectionTypeLoadException ex)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (Exception exSub in ex.LoaderExceptions)
                    {
                        sb.AppendLine(exSub.Message);
                        FileNotFoundException exFileNotFound = exSub as FileNotFoundException;
                        if (exFileNotFound != null)
                        {
                            if (!string.IsNullOrEmpty(exFileNotFound.FusionLog))
                            {
                                sb.AppendLine("Fusion Log:");
                                sb.AppendLine(exFileNotFound.FusionLog);
                            }
                        }
                        sb.AppendLine();
                    }
                    Logging.Warn("Failed to instantiate plugin at " + file.FullName + ":\n" + sb.ToString());
                }
            }
            return monitors;
        }

        /// <summary>
        /// Find all responders
        /// </summary>
        private List<EDDIResponder> findResponders()
        {
            DirectoryInfo dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            List<EDDIResponder> responders = new List<EDDIResponder>();
            Type pluginType = typeof(EDDIResponder);
            foreach (FileInfo file in dir.GetFiles("*Responder.dll", SearchOption.AllDirectories))
            {
                Logging.Debug("Checking potential plugin at " + file.FullName);
                try
                {
                    Assembly assembly = Assembly.LoadFrom(file.FullName);
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type.IsInterface || type.IsAbstract)
                        {
                            continue;
                        }
                        else
                        {
                            if (type.GetInterface(pluginType.FullName) != null)
                            {
                                Logging.Debug("Instantiating responder plugin at " + file.FullName);
                                EDDIResponder responder = type.InvokeMember(null,
                                                           BindingFlags.CreateInstance,
                                                           null, null, null) as EDDIResponder;
                                responders.Add(responder);
                            }
                        }
                    }
                }
                catch (BadImageFormatException)
                {
                    // Ignore this; probably due to CPU architecure mismatch
                }
                catch (ReflectionTypeLoadException ex)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (Exception exSub in ex.LoaderExceptions)
                    {
                        sb.AppendLine(exSub.Message);
                        FileNotFoundException exFileNotFound = exSub as FileNotFoundException;
                        if (exFileNotFound != null)
                        {
                            if (!string.IsNullOrEmpty(exFileNotFound.FusionLog))
                            {
                                sb.AppendLine("Fusion Log:");
                                sb.AppendLine(exFileNotFound.FusionLog);
                            }
                        }
                        sb.AppendLine();
                    }
                    Logging.Warn("Failed to instantiate plugin at " + file.FullName + ":\n" + sb.ToString());
                }
            }
            return responders;
        }
    }
}
