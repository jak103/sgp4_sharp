using NUnit.Framework;
using SGP4_Sharp;
using System.Collections.Generic;
using System;

namespace SGP4_Sharp
{
    [TestFixture()]
    public class PassPredict
    {
        #region Helpers

        public struct PassDetails
        {
            public DateTime aos;
            public DateTime los;
            public double max_elevation;

            public override string ToString()
            {
                return string.Format("AOS: {0}, LOS: {1}, Max El: {2}, Duration: {3}", aos.ToString(), los.ToString(), Util.RadiansToDegrees(max_elevation).ToString(), (los - aos).ToString());
            }

            public static PassDetails Parse(string passText)
            {
                PassDetails pass = new PassDetails();
                //AOS: 2015-4-13 0:5:39.0 UTC, LOS: 2015-4-13 0:16:18.0 UTC, Max El: 72.9662819114617, Duration: 0:10:39
                string[] tokens = passText.Split(new char[] { ',' }, 4);

                pass.aos = DateTime.Parse(tokens[0].Substring(5));
                pass.los = DateTime.Parse(tokens[1].Substring(5));
                pass.max_elevation = Util.DegreesToRadians(double.Parse(tokens[2].Substring(8)));

                return pass;
            }
        };

        public static DateTime FindCrossingPoint(CoordGeodetic user_geo, SGP4 sgp4, DateTime initial_time1, DateTime initial_time2, bool finding_aos)
        {
            Observer obs = new Observer(user_geo);

            bool running;
            int cnt;

            DateTime time1 = new DateTime(initial_time1.Ticks());
            DateTime time2 = new DateTime(initial_time2.Ticks());
            DateTime middle_time = null;

            running = true;
            cnt = 0;
            while (running && cnt++ < 16)
            {
                middle_time = time1.AddSeconds((time2 - time1).TotalSeconds() / 2.0);
                /*
             * calculate satellite position
             */
                Eci eci = sgp4.FindPosition(middle_time);
                CoordTopocentric topo = obs.GetLookAngle(eci);

                if (topo.elevation > 0.0)
                {
                    /*
                 * satellite above horizon
                 */
                    if (finding_aos)
                    {
                        time2 = middle_time;
                    }
                    else
                    {
                        time1 = middle_time;
                    }
                }
                else
                {
                    if (finding_aos)
                    {
                        time1 = middle_time;
                    }
                    else
                    {
                        time2 = middle_time;
                    }
                }

                if ((time2 - time1).TotalSeconds() < 1.0)
                {
                    /*
                 * two times are within a second, stop
                 */
                    running = false;
                    /*
                 * remove microseconds
                 */
                    int us = middle_time.Microsecond();
                    middle_time = middle_time.AddMicroseconds(-us);
                    /*
                 * step back into the pass by 1 second
                 */
                    middle_time = middle_time.AddSeconds(finding_aos ? 1 : -1);
                }
            }

            /*
         * go back/forward 1second until below the horizon
         */
            running = true;
            cnt = 0;
            while (running && cnt++ < 6)
            {
                Eci eci = sgp4.FindPosition(middle_time);
                CoordTopocentric topo = obs.GetLookAngle(eci);
                if (topo.elevation > 0)
                {
                    middle_time = middle_time.AddSeconds(finding_aos ? -1 : 1);
                }
                else
                {
                    running = false;
                }
            }

            return middle_time;
        }

        public static List<PassDetails> GeneratePassList(CoordGeodetic user_geo, SGP4 sgp4, DateTime start_time, DateTime end_time, int time_step)
        {
            List<PassDetails> pass_list = new List<PassDetails>();

            Observer obs = new Observer(user_geo);

            DateTime aos_time = null;
            DateTime los_time = null;

            bool found_aos = false;

            DateTime previous_time = new DateTime(start_time.Ticks());
            DateTime current_time = new DateTime(start_time.Ticks());

            while (current_time < end_time)
            {
                bool end_of_pass = false;

                /*
             * calculate satellite position
             */
                Eci eci = sgp4.FindPosition(current_time);

                CoordTopocentric topo = obs.GetLookAngle(eci);
              
                if (!found_aos && topo.elevation > 0.0)
                {
                    /*
                 * aos hasnt occured yet, but the satellite is now above horizon
                 * this must have occured within the last time_step
                 */
                    if (start_time == current_time)
                    {
                        /*
                     * satellite was already above the horizon at the start,
                     * so use the start time
                     */
                        aos_time = start_time;
                    }
                    else
                    {
                        /*
                     * find the point at which the satellite crossed the horizon
                     */
                        aos_time = FindCrossingPoint(
                            user_geo,
                            sgp4,
                            previous_time,
                            current_time,
                            true);
                    }
                    found_aos = true;
                }
                else if (found_aos && topo.elevation < 0.0)
                {
                    found_aos = false;
                    /*
                 * end of pass, so move along more than time_step
                 */
                    end_of_pass = true;
                    /*
                 * already have the aos, but now the satellite is below the horizon,
                 * so find the los
                 */
                    los_time = FindCrossingPoint(user_geo, sgp4, previous_time, current_time, false);

                    PassDetails pd = new PassDetails();
                    pd.aos = aos_time;
                    pd.los = los_time;
                    pd.max_elevation = FindMaxElevation(user_geo, sgp4, aos_time, los_time);

                    pass_list.Add(pd);
                }

                /*
             * save current time
             */
                previous_time = current_time;

                if (end_of_pass)
                {
                    /*
                 * at the end of the pass move the time along by 30mins
                 */
                    current_time = current_time.AddMinutes(30);
                }
                else
                {
                    /*
                 * move the time along by the time step value
                 */
                    current_time = current_time.AddSeconds(180);
                }

                if (current_time > end_time)
                {
                    /*
                 * dont go past end time
                 */
                    current_time = end_time;
                }
            }
            ;

            if (found_aos)
            {
                /*
             * satellite still above horizon at end of search period, so use end
             * time as los
             */
                PassDetails pd = new PassDetails();
                pd.aos = aos_time;
                pd.los = end_time;
                pd.max_elevation = FindMaxElevation(user_geo, sgp4, aos_time, end_time);

                pass_list.Add(pd);
            }

            return pass_list;
        }

        public static double FindMaxElevation(CoordGeodetic user_geo, SGP4 sgp4, DateTime aos, DateTime los)
        {
            Observer obs = new Observer(user_geo);

            bool running;

            double time_step = (los - aos).TotalSeconds() / 9.0;
            DateTime current_time = aos; //! current time
            DateTime time1 = aos; //! start time of search period
            DateTime time2 = los; //! end time of search period
            double max_elevation; //! max elevation

            running = true;

            do
            {
                running = true;
                max_elevation = -99999999999999.0;
                while (running && current_time < time2)
                {
                    /*
          * find position
          */
                    Eci eci = sgp4.FindPosition(current_time);
                    CoordTopocentric topo = obs.GetLookAngle(eci);

                    if (topo.elevation > max_elevation)
                    {
                        /*
            * still going up
            */
                        max_elevation = topo.elevation;
                        /*
            * move time along
            */
                        current_time = current_time.AddSeconds(time_step);
                        if (current_time > time2)
                        {
                            /*
              * dont go past end time
              */
                            current_time = time2;
                        }
                    }
                    else
                    {
                        /*
            * stop
            */
                        running = false;
                    }
                }

                /*
        * make start time to 2 time steps back
        */
                time1 = current_time.AddSeconds(-2.0 * time_step);
                /*
        * make end time to current time
        */
                time2 = current_time;
                /*
        * current time to start time
        */
                current_time = time1;
                /*
        * recalculate time step
        */
                time_step = (time2 - time1).TotalSeconds() / 9.0;
            } while (time_step > 1.0);


            return max_elevation;
        }

        private List<PassDetails> SetupKnownPasses()
        {
            List<PassDetails> passes = new List<PassDetails>();

            passes.Add(PassDetails.Parse("AOS: 4/13/2015 12:05:39 AM, LOS: 4/13/2015 12:16:18 AM, Max El: 72.9662819114617, Duration: 0:10:39"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 1:42:36 AM, LOS: 4/13/2015 1:52:38 AM, Max El: 22.2565140158341, Duration: 0:10:2"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 3:20:10 AM, LOS: 4/13/2015 3:29:33 AM, Max El: 14.3608087808289, Duration: 0:9:23"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 4:56:59 AM, LOS: 4/13/2015 5:07:07 AM, Max El: 24.8812838319532, Duration: 0:10:8"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 6:33:20 AM, LOS: 4/13/2015 6:43:51 AM, Max El: 55.156581296221, Duration: 0:10:31"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 8:10:53 AM, LOS: 4/13/2015 8:17:43 AM, Max El: 5.32236448706013, Duration: 0:6:50"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 9:39:21 PM, LOS: 4/13/2015 9:43:58 PM, Max El: 2.02758971989252, Duration: 0:4:37"));
            passes.Add(PassDetails.Parse("AOS: 4/13/2015 11:12:00 PM, LOS: 4/13/2015 11:22:22 PM, Max El: 35.2741789738443, Duration: 0:10:22"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 12:48:22 AM, LOS: 4/14/2015 12:58:45 AM, Max El: 31.6297414454579, Duration: 0:10:23"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 2:25:57 AM, LOS: 4/14/2015 2:35:23 AM, Max El: 14.8391729126458, Duration: 0:9:26"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 4:03:03 AM, LOS: 4/14/2015 4:12:52 AM, Max El: 18.825125182266, Duration: 0:9:49"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 5:39:26 AM, LOS: 4/14/2015 5:50:03 AM, Max El: 74.0719455689951, Duration: 0:10:37"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 7:16:16 AM, LOS: 4/14/2015 7:25:08 AM, Max El: 12.4996870879068, Duration: 0:8:52"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 10:18:38 PM, LOS: 4/14/2015 10:28:16 PM, Max El: 18.2935140103749, Duration: 0:9:38"));
            passes.Add(PassDetails.Parse("AOS: 4/14/2015 11:54:15 PM, LOS: 4/15/2015 12:04:52 AM, Max El: 52.3973867086772, Duration: 0:10:37"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 1:31:38 AM, LOS: 4/15/2015 1:41:18 AM, Max El: 16.9100893854387, Duration: 0:9:40"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 3:09:01 AM, LOS: 4/15/2015 3:18:32 AM, Max El: 15.7138023412645, Duration: 0:9:31"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 4:45:32 AM, LOS: 4/15/2015 4:56:01 AM, Max El: 40.5996122963757, Duration: 0:10:29"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 6:22:02 AM, LOS: 4/15/2015 6:31:59 AM, Max El: 24.1812087921171, Duration: 0:9:57"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 9:25:39 PM, LOS: 4/15/2015 9:33:52 PM, Max El: 9.10817960156249, Duration: 0:8:13"));
            passes.Add(PassDetails.Parse("AOS: 4/15/2015 11:00:16 PM, LOS: 4/15/2015 11:10:56 PM, Max El: 82.1837946273511, Duration: 0:10:40"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 12:37:17 AM, LOS: 4/16/2015 12:47:16 AM, Max El: 21.2008246719398, Duration: 0:9:59"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 2:14:50 AM, LOS: 4/16/2015 2:24:13 AM, Max El: 14.4443955221174, Duration: 0:9:23"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 3:51:35 AM, LOS: 4/16/2015 4:01:48 AM, Max El: 26.4411919922669, Duration: 0:10:13"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 5:27:57 AM, LOS: 4/16/2015 5:38:25 AM, Max El: 48.4822775571481, Duration: 0:10:28"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 7:05:40 AM, LOS: 4/16/2015 7:11:59 AM, Max El: 4.32559226934371, Duration: 0:6:19"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 8:33:25 PM, LOS: 4/16/2015 8:38:50 PM, Max El: 2.91680433892038, Duration: 0:5:25"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 10:06:28 PM, LOS: 4/16/2015 10:16:55 PM, Max El: 39.7611374230477, Duration: 0:10:27"));
            passes.Add(PassDetails.Parse("AOS: 4/16/2015 11:42:57 PM, LOS: 4/16/2015 11:53:17 PM, Max El: 29.5297313416365, Duration: 0:10:20"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 1:20:32 AM, LOS: 4/17/2015 1:29:57 AM, Max El: 14.6690480107132, Duration: 0:9:25"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 2:57:34 AM, LOS: 4/17/2015 3:07:26 AM, Max El: 19.6236118010221, Duration: 0:9:52"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 4:33:56 AM, LOS: 4/17/2015 4:44:34 AM, Max El: 82.6857963517039, Duration: 0:10:38"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 6:10:51 AM, LOS: 4/17/2015 6:19:28 AM, Max El: 11.0910877117058, Duration: 0:8:37"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 9:12:57 PM, LOS: 4/17/2015 9:22:44 PM, Max El: 20.3665615637994, Duration: 0:9:47"));
            passes.Add(PassDetails.Parse("AOS: 4/17/2015 10:48:42 PM, LOS: 4/17/2015 10:59:17 PM, Max El: 47.5374320453109, Duration: 0:10:35"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 12:26:07 AM, LOS: 4/18/2015 12:35:44 AM, Max El: 16.4480134589767, Duration: 0:9:37"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 2:03:27 AM, LOS: 4/18/2015 2:13:01 AM, Max El: 16.1079783540732, Duration: 0:9:34"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 3:39:56 AM, LOS: 4/18/2015 3:50:27 AM, Max El: 44.3818957685563, Duration: 0:10:31"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 5:16:28 AM, LOS: 4/18/2015 5:26:17 AM, Max El: 21.7274730713609, Duration: 0:9:49"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 8:19:48 PM, LOS: 4/18/2015 8:28:18 PM, Max El: 10.3331780917098, Duration: 0:8:30"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 9:54:35 PM, LOS: 4/18/2015 10:05:16 PM, Max El: 88.9197284694397, Duration: 0:10:41"));
            passes.Add(PassDetails.Parse("AOS: 4/18/2015 11:31:40 PM, LOS: 4/18/2015 11:41:36 PM, Max El: 20.3021905397441, Duration: 0:9:56"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 1:09:11 AM, LOS: 4/19/2015 1:18:36 AM, Max El: 14.561646908938, Duration: 0:9:25"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 2:45:54 AM, LOS: 4/19/2015 2:56:09 AM, Max El: 28.1007507632301, Duration: 0:10:15"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 4:22:15 AM, LOS: 4/19/2015 4:32:41 AM, Max El: 43.0119567507576, Duration: 0:10:26"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 6:00:11 AM, LOS: 4/19/2015 6:05:57 AM, Max El: 3.42714577481252, Duration: 0:5:46"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 7:27:17 PM, LOS: 4/19/2015 7:33:19 PM, Max El: 3.78630476752949, Duration: 0:6:2"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 9:00:40 PM, LOS: 4/19/2015 9:11:09 PM, Max El: 44.6412388690868, Duration: 0:10:29"));
            passes.Add(PassDetails.Parse("AOS: 4/19/2015 10:37:13 PM, LOS: 4/19/2015 10:47:30 PM, Max El: 27.7706266135631, Duration: 0:10:17"));
            return passes;
        }

        #endregion

        [Test()]
        public void PassPrediction()
        {
            List<PassDetails> knownPasses = SetupKnownPasses();
            CoordGeodetic geo = new CoordGeodetic(41.760612, -111.819384, 1.394);
            Tle tle = new Tle("ISS (ZARYA)",             
                          "1 25544U 98067A   15090.55997958  .00016867  00000-0  24909-3 0  9997",
                          "2 25544  51.6467 111.8303 0006284 156.4629 341.9393 15.55450652935952");
            SGP4 sgp4 = new SGP4(tle);

            /*
           * generate 7 day schedule
           */
            DateTime start_date = new DateTime(2015, 4, 13, 0, 0, 0);  //DateTime.Now (true);
            DateTime end_date = start_date.AddDays(7); //new DateTime (System.DateTime.Now.AddDays (7));

            List<PassDetails> pass_list = new List<PassDetails>();
            ;

            //Console.WriteLine("Start time: " + start_date.ToString());
            //Console.WriteLine("End time  : " + end_date.ToString());

            /*
           * generate passes
           */
            pass_list = GeneratePassList(geo, sgp4, start_date, end_date, 180);

            Assert.AreEqual(knownPasses.Count, pass_list.Count);
            for (int i = 0; i < pass_list.Count; ++i)
            {
                Assert.AreEqual(knownPasses[i].ToString(), pass_list[i].ToString());
            }
        }
    }
}

