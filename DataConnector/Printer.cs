#define debug
using System;
using System.Collections.Generic;
using Status.Common.Diagnostics;
using Opc.Ua;
using Status.DataModel.ObjectModel;
using Status.DataModel.ObjectModel.Common;
using System.Threading;
using System.IO;

namespace TestFunction
{
    public class DataConnector//named as such as it is designed to mimic the behavior (to an outside viewer) of a Status Enterprise Data Connector
    {
        private static StatusServerClient _statusServerClient = null;
        private static StatusObjectModel _model = null; //represents the entire data model held within the server
        private static int timeoutlength = 60000;//default value, keeping here to easily access
        private static string usr, pwd; //credentials for connecting to the server
        //private static System.Timers.Timer session;//used for reconnecting to server
        private static Position current, target, lims;//positions representing the current location, the target location, and the maximum coordinates
        private static int speed = 6000;//default value
        private static int maxspeed = 12000;//mm/min
        private static DateTime prev;//used for integral control (i.e. we have speed; to get position change, we need speed*time difference)
        public static Queue<string> commands = new Queue<string>();//, subcommands=new Queue<string>();//commands is passed down from the server; subcommands is similar, but with complex jobs broken down
        public static bool running = true, writing = true;//booleans for continuing/terminating looping threads
        private static Mutex mut = new Mutex();//for the command queue
        private static string view = "Objects/User View/My simulation/Printer001";//root 'folder' for the printer simulation to access on the server; represents the printer object in the data model
        private static TimeSpan len = new TimeSpan(0);//represents the predicted amount of time remaining for the entire set of tasks in the queue
        private static NodeId funccalled, xid, yid, zid, timeid, lengthid, xlim, ylim, zlim, outid, commandid;//handles for specific fields on the server
        private static Queue<TimeSpan> times = new Queue<TimeSpan>();
        private static byte busy = 0;
        //private static Thread write = new Thread(WriteLoc);

        public static void Main(string[] args)
        {
            current = new Position();//initializes to <0,0,0>
            target = new Position();
            // initializeTimer(); //currently uses all server connections simultaneously; see method description

            //if program is running in release, the program must be called with these details provided by the user
#if debug
            args = new string[] { "Administrator", "Passwd" };
#else
            if (args.Length < 2)
                throw failure;
#endif
            usr = args[0];
            pwd = args[1];
            Connect(usr, pwd);
            initIds();//this must be called before any calls to server are made; they all depend on the NodeIDs retreived from this function;
            writeOut("");//reset output field
            lims = getLims();//retrieve limits from server
            Thread th = new Thread(takeInput);//take input from the server as a background process
            th.Start();

            Thread th2 = new Thread(writeTime);//write time remaining to server
            th2.Start();
            while (true) //continually executes tasks in the queue, if they are present
            {

                if (commands.Count != 0)
                {
                    mut.WaitOne();
                    string com = commands.Dequeue();
                    busy = 1;
                    writeLength();
                    mut.ReleaseMutex();
                    string _out = parseInput(com);
                    busy = 0;
                    if(times.Count!=0)
                        times.Dequeue();
                    rebuildTime();
                    writeOut(_out);
                }
                else
                {
                    len = new TimeSpan(0);
                    Thread.Sleep(500);//sleep 0.5 seconds
                }
            }
        }

        /*
         * Never got this to work with the original connection closing; using this filled up all open connection slots
        //function called when timer goes off to re-establish server connection
        private static void Reconnect(Object sender, EventArgs e)
        {
            session.Enabled = false;
            Connect(usr, pwd);
        }

        //sets a timer for re-establishing server connection; needs to be rewritten so counter resets every time a new server call is made(?)
        private static void initializeTimer()
        {
            session = new System.Timers.Timer();
            session.Interval = timeoutlength * 9 / 10;//10% 'buffer' zone
            session.Elapsed += Reconnect;
            session.AutoReset = true;
            //don't enable here, should be enabled only at the end of Connect method
        }
        */

        //Makes connection to server
        private static void Connect(string usr, string pwd)
        {
            _statusServerClient = new StatusServerClient(usr,
                                       pwd,
                                       "opc.tcp://finoti:62542/StatusEnterprise",
                                       "English",
                                       AuthenticationType.Username,
                                       null,
                                       MessageSecurityMode.None,
                                       SecurityPolicy.None,
                                       5000,
                                       10000,
                                       timeoutlength,
                                       5000,
                                       true);
            if (_statusServerClient == null) //if login with username fails, attempt anonymous login
            {
                Console.WriteLine("User Credentials failed, attempting Anonymous Login");
                _statusServerClient = new StatusServerClient("",
                            "",
                            "opc.tcp://finoti:62542/StatusEnterprise",
                            "English",
                            AuthenticationType.Anonymous,
                            null,
                            MessageSecurityMode.None,
                            SecurityPolicy.None,
                            5000,
                            10000,
                            timeoutlength,
                            5000,
                            true);
                if (_statusServerClient == null)
                {
                    Close("Error creating client object");
                    return;
                }
            }
            _statusServerClient.AlwaysUseMainEndpoint = false;
            SResult sresult = _statusServerClient.Connect();
            if (sresult.IsNotGood())
            {
                Close(sresult.Message);
                return;
            }
            sresult = _statusServerClient.GetObjectModel(out _model);
            if (sresult.IsNotGood())
            {
                Close(sresult.Message);
                return;
            }
        }

        //exits the program with an error message about not being able to establish the connection to the server
        private static void Close(string err)
        {
#if debug
            Console.WriteLine("Could not connect to server; application terminated\nError: "+err);
#endif
            Environment.Exit(-1);
        }

        //repeatedly writes current location to the server as a background service
        private static void WriteLoc()
        {
            while (writing)
            {
                WriteOnce();
            }
        }

        //writes the current location to the server
        private static void WriteOnce()
        {
            double x = current.x, y = current.y, z = current.z;
            try
            {
                _model.WriteValue(xid, 13, 0, x, DateTime.Now);
                _model.WriteValue(yid, 13, 0, y, DateTime.Now);
                _model.WriteValue(zid, 13, 0, z, DateTime.Now);
                Thread.Sleep(100);
            }
            catch(NullReferenceException)
            {
                Console.WriteLine("Could not write data to server, attempting reconnect");
                //Connect(usr,pwd);
            }
            
        }

        //moves the print head to the designated location
        private static void Move()
        {
            prev = DateTime.Now;
            if (target == null || current == null)
                return;
            Thread write = new Thread(WriteLoc);
            writing = true;
            write.Start();
            while (Position.dist(current, target) > speed / 6000.0)
            {
                double mov = speed * (DateTime.Now.Ticks - prev.Ticks) / 60.0 / TimeSpan.TicksPerSecond;
                prev = DateTime.Now;
                double dist = Position.dist(target, current);
                current.x += mov / dist * (target.x - current.x);
                current.y += mov / dist * (target.y - current.y);
                current.z += mov / dist * (target.z - current.z);
                Thread.Sleep(10);//wait 0.01 seconds 
            }
            current.x = target.x;
            current.y = target.y;
            current.z = target.z;
            writing = false;
            write.Join();
            WriteOnce();
            
        }

        //gets the contents of the command from the server
        private static string getCommand()
        {
            ObjectBase obj;
            try
            {
                _model.ObjectFromNodeId(commandid, out obj);
                return (string)obj.Value;
            }
            catch (NullReferenceException)
            {
                Console.WriteLine("Could not read data from server, attempting reconnect");
                return "";
            }
        }

        //checks to see if a command has been issued to the machine
        private static bool CallMade()
        {
            ObjectBase obj;//the documentation is unclear on whether an ObjectBase is updated by the server after initialization. Assuming not, this should be created with each function call
            try
            {
                _model.ObjectFromNodeId(funccalled, out obj);
                return (bool)obj.Value;
            }
            catch (NullReferenceException)
            {
                Console.WriteLine("Could not read data from server, attempting reconnect");
                return false;
            }
        }

        //resets input fields, so clients can send more commands. called from takeInput thread
        private static void ResetCall()
        {
            try
            {
                _model.WriteValue(funccalled, 13, 0, false, DateTime.Now);
                _model.WriteValue(commandid, 13, 0, "", DateTime.Now);
            }
            catch (NullReferenceException)
            {
                Console.WriteLine("Could not write data to server, attempting reconnect");
            }

        }

        //function running as separate thread in background to add commands to a queuea
        private static void takeInput()
        {
            while (running)
            {
                if (CallMade())
                {
                    mut.WaitOne();
                    string s = getCommand();
                    commands.Enqueue(s);
                    mut.ReleaseMutex();
                    parseComments(s);
                    ResetCall();
                    mut.WaitOne();
                    writeLength();
                    mut.ReleaseMutex();
                }
            }
        }

        //parses input string, calls function based on input
        private static string parseInput(string _in)
        {
            if (_in == null || _in == "")
            {
                return "Invalid Command";
            }
            string _out = "OK";
            string[] arr = _in.Trim().Split(' ');//break space-delineated command into array, ignoring leading and trailing whitespace

            //remove empty entries
            List<string> tmp = new List<string>(arr);
            tmp.RemoveAll(new Predicate<string>("".Equals));//clears empty values; not sure if should be empty string or single space
            arr = tmp.ToArray();

            //process commands
            switch (arr[0].ToUpper())
            {
                //the machine can take GCode directly
                case "G1"://parse G1 and G0 the same
                case "G0":
                    for (int i = 1; i < arr.Length; i++)
                    {
                        if (arr[i].Length != 0)
                            switch (arr[i][0])
                            {
                                case 'X':
                                    target.x = Convert.ToDouble(arr[i].Trim().Substring(1));
                                    break;
                                case 'Y':
                                    target.y = Convert.ToDouble(arr[i].Trim().Substring(1));
                                    break;
                                case 'Z':
                                    target.z = Convert.ToDouble(arr[i].Trim().Substring(1));
                                    break;
                                case 'F'://speed
                                    speed = (int)Convert.ToDouble(arr[i].Trim().Substring(1));
                                    if (speed > maxspeed)
                                    {
                                        speed = maxspeed;
                                        _out = String.Format("Maximum speed ({0} mm/min) exceeded; setting speed to {0} mm/min", maxspeed);
                                    }
                                    break;
                                case 'E'://extruder movement support would go here
                                case ';':
                                    break;
                                default:
                                    return "Invalid parameter: "+arr[i];
                            }
                    }
                    Move();
                    break;

                //generic print job
                case "PRINT":
                    try
                    {
                        //file type check
                        if (!(arr[1].Substring(arr[1].Length - 3).Equals("gco") || arr[1].Substring(arr[1].Length - 5).Equals("gcode")))
                        {
                            Console.WriteLine("Unrecognized file type; file type must be .gco or .gcode");
                            break;
                        }
                        string[] file;
                        try
                        {
                            file = File.ReadAllLines(arr[1]);//tries to read entire G-Code file
                        }
                        catch (FileNotFoundException e)
                        {
#if debug
                            Console.WriteLine("File not found; make sure directory and file name are correctly spelled");
#endif
                            return e.Message;
                        }

                        tmp = new List<string>(file);//use same tmp variable from earlier
                        tmp.RemoveAll(new Predicate<string>("".Equals));
                        file = tmp.ToArray();

                        //executes every line recursively, limited to one layer of recursion
                        foreach (string s in file)
                        {
                            if (s.Length > 5 && s.Substring(0, 5).ToLower().Equals("print"))//recursion failsafe
                                break;
                            //the following has been moved to a separate function which runs when the job is added to the queue, rather than when
                            //the function is executed
                            // if (s[0] == ';')
                            //{
                            //    len=len.Add(tryTime(s.Substring(1).Trim()));
                            //}
                            Console.WriteLine(parseInput(s));//write nested output to console
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message);
                    }
                    break;
                //'homing'
                case "G28":
                    target.x = 0;
                    target.y = 0;
                    target.z = 0;
                    Move();
                    break;
                case ";"://standard g-code comment character; this one shouldn't be called ever, unless someone sends a comment straight to the server for some reason. 
                    //these should all be encapsulated in the nested print loop under case "print", but this is a failsafe
                    break;
                default:
                    return "Command not recognized: " + _in;
            }
            return _out;
        }

        //checks comments of file for print time prediction
        private static void parseComments(string input)
        {
            if (input == null || input.Length < 6 || !input.Substring(0, 5).ToLower().Equals("print"))
                return;
            string[] file;
            try
            {
                file = File.ReadAllLines(input.Substring(input.IndexOf(' ')+1));//tries to read entire G-Code file
            }
            catch (FileNotFoundException e)
            {
#if debug
                Console.WriteLine("Could not open file to parse comments; " + e.Message);
#endif
                return;
            }
            List<string> tmp = new List<string>(file);//use same tmp variable from earlier
            tmp.RemoveAll(new Predicate<string>("".Equals));
            file = tmp.ToArray();
            foreach (string s in file)
            {
                //if (s.Length > 5 && s.Substring(0, 5).ToLower().Equals("print"))//recursion failsafe
                //    break;
                if (s[0] == ';')
                {
                    TimeSpan t = tryTime(s.Substring(1).Trim());
                    times.Enqueue(t);
                    len = len.Add(t);
                    if(t.Ticks!=0)//if a time was successfully read
                        return;//don't parse any more strings
                }
            }
        }

        //writes output string to the output field on the server
        private static void writeOut(string _out)
        {
            try
            {
                _model.WriteValue(outid, 13, 0, _out, DateTime.Now);
            }
            catch(NullReferenceException)
            {
                Console.WriteLine("Could not write data to server, attempting reconnect");
            }
        }

        //get geometric constraints of hotend position
        private static Position getLims()
        {
            ObjectBase x, y, z;
            try
            {
                _model.ObjectFromNodeId(xid, out x);
                _model.ObjectFromNodeId(yid, out y);
                _model.ObjectFromNodeId(zid, out z);
                return new Position((double)x.Value, (double)y.Value, (double)z.Value);
            }
            catch (NullReferenceException)
            {
                Console.WriteLine("Could not read data from server, attempting reconnect");
                return new Position();
            }

            
        }

        //try to glean the time estimation of a single print file from a provided line of input
        private static TimeSpan tryTime(string input)
        {
            TimeSpan o = new TimeSpan(0);
            if (input.Length > 4 && (input.Substring(0, 4).ToLower().Equals("time")) || (input.Length > 10 && input.Substring(0, 10).ToLower().Equals("print time")))
            {
                string[] tmp = input.Split(' ');
                short[] times = Array.ConvertAll<string, short>(tmp[tmp.Length - 1].Split(':'), Convert.ToInt16);
                o = new TimeSpan(times[0], times[1], times.Length == 3 ? times[2] : 0);
            }
            return o;
        }

        //Writes queue length to server
        private static void writeLength()
        {
            try
            {
                _model.WriteValue(lengthid, 13, 0, commands.Count+busy, DateTime.Now);
            }
            catch (NullReferenceException e)
            {
                Console.WriteLine("Could not write data to server, attempting reconnect");
            }
        }

        //Writes estimated remaining queue time to server (runs in separate thread)
        private static void writeTime()
        {
            int decrement = 1000;//ms
            TimeSpan sub = new TimeSpan(0, 0, 0, 0, decrement);
            while (running)
            {
                try
                {
                    _model.WriteValue(timeid, 13, 0, len.ToString(), DateTime.Now);
                }
                catch (NullReferenceException e)
                {
                    Console.WriteLine("Could not write data to server, attempting reconnect");
                }
                Thread.Sleep(decrement);
                len=len.TotalMilliseconds>decrement?len.Subtract(sub):new TimeSpan(0);
            }
        }

        //initializes the server NodeIDs for all the relevant values
        private static void initIds()
        {
                _model.GetNodeIdFromBrowsePath(view + "/FuncCallMade", out funccalled);
                _model.GetNodeIdFromBrowsePath(view + "/XPos", out xid);
                _model.GetNodeIdFromBrowsePath(view + "/YPos", out yid);
                _model.GetNodeIdFromBrowsePath(view + "/ZPos", out zid);
                _model.GetNodeIdFromBrowsePath(view + "/Time Remaining", out timeid);
                _model.GetNodeIdFromBrowsePath(view + "/QueueLength", out lengthid);
                _model.GetNodeIdFromBrowsePath(view + "/XLim", out xlim);
                _model.GetNodeIdFromBrowsePath(view + "/YLim", out ylim);
                _model.GetNodeIdFromBrowsePath(view + "/ZLim", out zlim);
                _model.GetNodeIdFromBrowsePath(view + "/Output", out outid);
                _model.GetNodeIdFromBrowsePath(view + "/Command", out commandid);
        }

        //constructs the time from the queue of timespans
        private static void rebuildTime()
        {
            len = new TimeSpan();
            foreach(TimeSpan t in times)
            {
                len = len.Add(t);
            }
        }
    }

    //simply a container class for a 3-axis cartesian coordinate system with functions for comparing locations
    class Position
    {
        public double x, y, z;

        //default to position <0,0,0>
        public Position(double xin = 0, double yin = 0, double zin = 0)
        {
            x = xin; y = yin; z = zin;
        }

        public static bool operator ==(Position p1, Position p2)
        {
            if (Object.ReferenceEquals(p1, p2))//if they both point to the same Position variable, or if both are null
                return true;
            if ((object)p1 == null || (object)p2 == null)//if only one is null (cast to object to avoid recursively referencing this method)
                return false;
            return p1.x == p2.x && p1.y == p2.y && p1.z == p2.z;
        }

        public static bool operator !=(Position p1, Position p2)
        {
            return !(p1 == p2);
        }

        //returns distance between two positions in 3D cartesian space
        public static double dist(Position p1, Position p2)
        {
            return Math.Sqrt(Math.Pow(p1.x - p2.x, 2) + Math.Pow(p1.y - p2.y, 2) + Math.Pow(p1.z - p2.z, 2));
        }

        public override bool Equals(object o)
        {
            return this == (Position)o;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}