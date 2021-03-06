﻿using EddiDataDefinitions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Utilities;

namespace EddiEvents
{
    public class StarScannedEvent : Event
    {
        public const string NAME = "Star scanned";
        public const string DESCRIPTION = "Triggered when you complete a scan of a stellar body";
        public static string SAMPLE = "{ \"timestamp\":\"2016-10-05T10:13:55Z\", \"event\":\"Scan\", \"BodyName\":\"Col 285 Sector RS-K c8-5 A\", \"DistanceFromArrivalLS\":0.000000, \"StarType\":\"TTS\", \"StellarMass\":0.449219, \"Radius\":458926400.000000, \"AbsoluteMagnitude\":8.287720, \"Age_MY\":51, \"SurfaceTemperature\":3209.000000, \"SemiMajorAxis\":352032544.000000, \"Eccentricity\":0.027010, \"OrbitalInclination\":74.195038, \"Periapsis\":330.750244, \"OrbitalPeriod\":36441.519531, \"RotationPeriod\":203102.843750 }";

        public static Dictionary<string, string> VARIABLES = new Dictionary<string, string>();

        static StarScannedEvent()
        {
            VARIABLES.Add("name", "The name of the star that has been scanned");
            VARIABLES.Add("chromaticity", "The apparent colour of the star that has been scanned");
            VARIABLES.Add("stellarclass", "The stellar class of the star that has been scanned (O, G, etc)");
            VARIABLES.Add("solarmass", "The mass of the star that has been scanned, relative to Sol's mass");
            VARIABLES.Add("massprobability", "The probablility of finding a star of this class and at least this mass");
            VARIABLES.Add("radius", "The radius of the star that has been scanned, in metres");
            VARIABLES.Add("solarradius", "The radius of the star that has been scanned, compared to Sol");
            VARIABLES.Add("radiusprobability", "The probablility of finding a star of this class and at least this radius");
            VARIABLES.Add("absolutemagnitude", "The absolute magnitude of the star that has been scanned");
            VARIABLES.Add("luminosity", "The luminosity of the star that has been scanned");
            VARIABLES.Add("luminosityprobability", "The probablility of finding a star of this class and at least this luminosity");
            VARIABLES.Add("age", "The age of the star that has been scanned, in years (rounded to millions of years)");
            VARIABLES.Add("temperature", "The temperature of the star that has been scanned");
            VARIABLES.Add("distancefromarrival", "The distance in LS from the main star");
            VARIABLES.Add("orbitalperiod", "The number of seconds taken for a full orbit of the main star");
            VARIABLES.Add("rotationperiod", "The number of seconds taken for a full rotation");
            VARIABLES.Add("semimajoraxis", "");
            VARIABLES.Add("eccentricity", "");
            VARIABLES.Add("orbitalinclination", "");
            VARIABLES.Add("periapsis", "");
            VARIABLES.Add("rings", "The star's rings");
        }

        public string name { get; private set; }

        public string stellarclass { get; private set; }

        public decimal solarmass{ get; private set; }

        public decimal massprobability { get; private set; }

        public decimal radius { get; private set; }

        public decimal solarradius { get; private set; }

        public decimal radiusprobability { get; private set; }

        public decimal absolutemagnitude { get; private set; }

        public decimal luminosity { get; private set; }

        public decimal luminosityprobability { get; private set; }

        public long age { get; private set; }

        public decimal temperature { get; private set; }

        public string chromaticity { get; private set; }

        public decimal distancefromarrival { get; private set; }

        public decimal? orbitalperiod { get; private set; }

        public decimal rotationperiod { get; private set; }

        public decimal? semimajoraxis { get; private set; }

        public decimal? eccentricity { get; private set; }

        public decimal? orbitalinclination { get; private set; }

        public decimal? periapsis { get; private set; }

        public List<Ring> rings { get; private set; }

        public StarScannedEvent(DateTime timestamp, string name, string stellarclass, decimal solarmass, decimal radius, decimal absolutemagnitude, long age, decimal temperature, decimal distancefromarrival, decimal? orbitalperiod, decimal rotationperiod, decimal? semimajoraxis, decimal? eccentricity, decimal? orbitalinclination, decimal? periapsis, List<Ring> rings) : base(timestamp, NAME)
        {
            this.name = name;
            this.stellarclass = stellarclass;
            this.solarmass = solarmass;
            this.radius = radius;
            this.absolutemagnitude = absolutemagnitude;
            this.age = age;
            this.temperature = temperature;
            this.distancefromarrival = distancefromarrival;
            this.orbitalperiod = orbitalperiod;
            this.rotationperiod = rotationperiod;
            this.semimajoraxis = semimajoraxis;
            this.eccentricity = eccentricity;
            this.orbitalinclination = orbitalinclination;
            this.periapsis = periapsis;
            this.rings = rings;
            StarClass starClass = StarClass.FromName(this.stellarclass);
            if (starClass != null)
            {
                this.massprobability = sanitiseCP(starClass.stellarMassCP(solarmass));
                this.solarradius = StarClass.solarradius(radius);
                this.radiusprobability = sanitiseCP(starClass.stellarRadiusCP(this.solarradius));
                this.luminosity = StarClass.luminosity(absolutemagnitude);
                this.luminosityprobability = sanitiseCP(starClass.luminosityCP(this.luminosity));
                // TODO remove when temperature is in journal
                this.temperature = StarClass.temperature(luminosity, this.radius);
                this.chromaticity = starClass.chromaticity;
            }

        }

        private decimal sanitiseCP(decimal cp)
        {
            // Trim decimal places appropriately
            if (cp < .00001M || cp > .9999M)
            {
                return Math.Round(cp * 100, 4);
            }
            else if (cp < .0001M || cp > .999M)
            {
                return Math.Round(cp * 100, 3);
            }
            else if (cp < .001M || cp > .99M)
            {
                return Math.Round(cp * 100, 2);
            }
            else
            {
                return Math.Round(cp * 100);
            }
        }
    }
}
