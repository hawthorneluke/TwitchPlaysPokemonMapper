using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Diagnostics;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Structure;
using System.IO;
using System.Drawing.Drawing2D;

namespace twitchPlaysPokemon
{
    public partial class Form1 : Form
    {
        const string windowTitle = "TwitchPlaysPokemon"; //title of stream window
        const int tileSize = 16; //tile size in pixels
        const int scoreThresh = 60; //places found on the map with a score lower than this are ignored (perfect score is 10 tiles * 9 tiles = 90)
        const int playedLocatedCountThresh = 3; //number of times in a row the player must be found in the middle of the screen in captures to progress with the tracking (via player being a black tile at screen tile x=4, y=4 (starting from 0)) - used to ignore battles etc
        const int foundCountThresh = 3; //number of times in a row the current screen must be located on the map before any data is saved (works with recentLocationX/Y to make sure the located screen is real via it moving around a realistically limited area on the full map, ignoring map sections that'd be far away from possibly reachable time size)
        const int captureTimeInterval = 2000; //interval in time (ms) between captures while waiting to reach playerLocatedCountThresh
        const int findTimeInterval = 4000; //interval in time (ms) between doing captures (and then locating on map) after getting the required successful captures
        const int outputLinesThresh = 1000; //number of lines to log in richTextBox
        const int scoreGiveUpThresh = -5; //score at which to skip to next locaiton when locating screen on map (using this to give up tile comparisons and skip to next location saves a LOT of time in total)
        const double okDistFromRecentLocationMultipier = 2; //when testing to see if a located location is close enough to recentLocationX/Y, this is used as the multiplier on top of 1 sec = 1 tile moved away from recentLocationX/Y
        const double colourLoopTimeHours = 24; //number of hours it takes to loop through all colours when drawing the path on the map
        const int lineThickness = 8; //thickness of path line drawn on map

        const int penAlpha = 50; //pen alpha to draw the path when generating the map

        const double recentLocationMultiplier = 0.5; //multiplier applied to recentLocaitonX/Y when moving it

        int lastX, lastY;
        double recentLocationX, recentLocationY; //used to save the general screen location on the map. Locations located on the map too far from this are ignored. "too far" is determined by the time since the last location was "correctly" located

        MCvFont font = new MCvFont(FONT.CV_FONT_HERSHEY_PLAIN, 1, 1); //font for writing (used in debug)

        int[,] dataMap; //holds the tile data of the map. Tile data is 0 to 3. 0 being a rather white 16x16 block of pixels, 1 being a bit grey, 2 being darker grey and 3 being quite black. The screen is located by comparing these tile blocks.

        const string fnameRoot = ""; //root of files
        const string fnameScreen = fnameRoot + "screen.bmp"; //screen capture save file name
        const string fnameText = "out.txt"; //data output file name

        string fnameMap; //file name of the map file


        int playedLocatedCount = 0; //used to count up to playerLocatedCountThresh
        int foundCount = 0; //used to count up to foundCountThresh

        DateTime start; //time started capturing
        DateTime lastFindTime; //last time screen was located. Used to work out how far player can be from recentLocationX/Y
        TimeSpan timeSinceLastFind; //time span of above

        int mapWidth, mapHeight;

        int twitchViewX, twitchViewY, twitchViewWidth, twitchViewHeight; //coords of game screen in twitch stream

        int outputLines = 0; //to count the lines logged in the richTextBox (to clear once it reaches its threshold)


        private void Form1_Load(object sender, EventArgs e)
        {
            openFileDialog1.ShowDialog();
            fnameMap = openFileDialog1.FileName;

            Image<Gray, Byte> imageMap = new Image<Gray, Byte>(fnameMap);
            //imageOutput = new Image<Bgr, Byte>(fnameMap);

            mapWidth = imageMap.Width;
            mapHeight = imageMap.Height;

            //set recentLocationX/Y to map center. This can be relocated with the Set Loc button
            recentLocationX = lastX = (mapWidth / tileSize) / 2;
            recentLocationY = lastY = (mapHeight / tileSize) / 2;

            dataMap = getData(imageMap); //create dataMap

            imageMap.Dispose(); //no need for the image itself anymore

            start = DateTime.Now; //start time. Used to work out time span since this time for every saved location

            setTwitchView(); //set game output coords in twitch stream for screen capture
        }

        //start timer1 (that does all the work) and reset needed vars
        private void buttonStart_Click(object sender, EventArgs e)
        {
            timer1.Start();

            lastFindTime = DateTime.Now;
            timeSinceLastFind = TimeSpan.Zero;
        }

        //timer tick. Do work here
        private void timer1_Tick(object sender, EventArgs e)
        {
            //start new thread
            BackgroundWorker bgw = new BackgroundWorker();
            bgw.DoWork += backgroundWorker1_DoWork;
            bgw.RunWorkerAsync(); //do work in thread

            //update time label
            DateTime now = DateTime.Now;
            TimeSpan diff = now - start;
            labelTime.Text = (int)diff.TotalDays + "d " + diff.Hours + "h " + diff.Minutes + "m " + diff.Seconds + "s";
        }

        //ouput (save) a screen capture to open in paint etc to measure the correct coords to use
        private void button1_Click(object sender, EventArgs e)
        {
            //this used to load in a captured screen image set at the right (160x144) resolution to locate on the map and draw a red rectangle where it thought it was
            /*
            openFileDialog1.ShowDialog();
            string fnameCapture = openFileDialog1.FileName;

            Image<Gray, Byte> imageCapture = new Image<Gray, Byte>(fnameCapture);

            doWork(imageCapture);

            imageCapture.Dispose();
            */

            getScreen(fnameRoot + "twitchView.bmp");

            Image<Gray, Byte> imageCapture = new Image<Gray, Byte>(fnameRoot + "twitchView.bmp");

            //crop and resize and output test.png to show the final image used in the screen location
            Rectangle rect = new Rectangle(new Point(twitchViewX, twitchViewY), new Size(twitchViewWidth, twitchViewHeight));
            imageCapture.ROI = rect;
            imageCapture = imageCapture.Resize(160, 144, INTER.CV_INTER_LINEAR);
            imageCapture.Save(fnameRoot + "test.png");
        }

        //creates and returns the tile data of the map or screen (supplied via the image param)
        private int[,] getData(Image<Gray, Byte> image)
        {
            int[,] data = new int[image.Height / tileSize, image.Width / tileSize];

            //for loop over all tiles
            for (int y = 0; y < image.Height / tileSize; y++)
            {
                for (int x = 0; x < image.Width / tileSize; x++)
                {
                    //set default data to 0, which is a rather white collection of pixels in this tile
                    data[y, x] = 0; //going with openCV's y first then x in the 2D array way of doings things

                    //each pixel of each tile will be looked at here and depending on its shade of grey, the scores below will be increased
                    int scoreBlack = 0;
                    int scoreDarkGrey = 0;
                    int scoreLightGrey = 0;
                    bool done = false;
                    //for loop over all pixels in tile
                    for (int y2 = 0; y2 < tileSize; y2++)
                    {
                        for (int x2 = 0; x2 < tileSize; x2++)
                        {
                            byte a = image.Data[(y * tileSize) + y2, (x * tileSize) + x2, 0]; //get pixel grey shade

                            //set score depending on shade and set data value to 1,2 or 3 depending on total score
                            if (a < 50)
                            {
                                scoreBlack++;
                                if (scoreBlack >= 20)
                                {
                                    data[y, x] = 3; //a black tile of pixels
                                    done = true;
                                    break;
                                }
                            }
                            else if (a < 100)
                            {
                                scoreDarkGrey++;
                                if (scoreDarkGrey >= 80)
                                {
                                    data[y, x] = 2; //a dark grey tile of pixels
                                    done = true;
                                    break;
                                }
                            }
                            else if (a < 200)
                            {
                                scoreLightGrey++;
                                if (scoreLightGrey >= 100)
                                {
                                    data[y, x] = 1; //a light grey tile of pixels (if this isn't triggered, then data stays as 0 - white
                                    done = true;
                                    break;
                                }
                            }
                        }
                        if (done) break;
                    }
                }
            }

            return data;
        }

        //the actual screen location work
        private bool doWork(Image<Gray, Byte> imageCapture)
        {
            int[,] dataCapture = getData(imageCapture); //get tile data of captured screen
            setText(dataCapture[4, 4].ToString() + " "); //output tile shade value of middle of the screen (where player should be, and if they are, this should be 3)
            if (dataCapture[4, 4] != 3)  //if "player" is not found in middle of the screen (in battle etc)
            {
                playedLocatedCount = 0;
                return false; //try locating the player again
            }
            else
            {
                playedLocatedCount++;
            }

            if (playedLocatedCount < playedLocatedCountThresh) //player location count not reached threshold yet?
            {
                return false;
            }


            double maxScore = Int16.MinValue; //best score of located place in map out of all possible locations
            int maxY = 0; //x value of location with said score
            int maxX = 0; //y value of location with said score

            //start going over all the tiles
            //THIS CAN BE OPTIMIZED by only going over the tiles around recentLocationX/Y (taking timeSinceLastFind into account for the area size like used below)
            //this starts and finishes half a screen out of bounds because some maps I was testing with had parts (area boundaries) cut off of them, even though they actually show up on screen in game
            for (int y = -(imageCapture.Height / 2) / tileSize; y < (mapHeight - (imageCapture.Height / 2)) / tileSize; y++)
            {
                for (int x = -(imageCapture.Width / 2) / tileSize; x < (mapWidth - (imageCapture.Width / 2)) / tileSize; x++)
                {
                    bool skip = false;

                    double score = 0; //score of this location
                    for (int y2 = 0; y2 < imageCapture.Height / tileSize; y2++) //go over tiles of the screen capture
                    {
                        for (int x2 = 0; x2 < imageCapture.Width / tileSize; x2++)
                        {
                            if (y + y2 < 0 || x + x2 < 0 || y + y2 >= mapHeight / tileSize || x + x2 >= mapWidth / tileSize) continue; //obviously can't compare out of bound tiles

                            if (dataCapture[y2, x2] == dataMap[y + y2, x + x2]) //does the data value match for this tile?
                            {
                                score++;
                            }
                            else
                            {
                                score--;
                                if (score < scoreGiveUpThresh) //give up if score is too low (this saves a LOT of time)
                                {
                                    skip = true;
                                    break;
                                }
                            }
                        }
                        if (skip) break;
                    }


                    //debug (draw locations with score > 40 onto map)
                    /*
                    if (score > 40)
                    {
                        rect = new Rectangle(new Point(x * tileSize, y * tileSize), new Size(imageCapture.Width, imageCapture.Height));
                        imageOutput.Draw(rect, new Bgr(Color.Red), 4);
                    }
                    */

                    if (score > maxScore) //only keep track of location with biggest score
                    {
                        maxScore = score;
                        maxY = y;
                        maxX = x;
                    }
                }
            }

            //only want locations that have a score greather than a threshold
            if (maxScore > scoreThresh)
            {
                setText("\nmaxX: " + maxX + ", maxY: " + maxY + ", lastX: " + lastX + ", lastY: " + lastY + "\n");
                if (!(maxX == lastX && maxY == lastY)) //ignore location if it's the same as the last one (a real player is generally moving. An incorrect location discovery isn't)
                {
                    lastX = maxX;
                    lastY = maxY;

                    timeSinceLastFind = DateTime.Now - lastFindTime;

                    //move recentLocationX/Y closer to this found location, but not too close so to make troll locations not have too much effect (this means it'll take a while to locate the player at first without using the Set Loc button, but deters way incorrect location finds that are too far from the player to be possible - if only the area around this location is searched through in the for loops above screen locating WILL SPEED UP GREATLY)
                    recentLocationX += (maxX - recentLocationX) * recentLocationMultiplier;
                    recentLocationY += (maxY - recentLocationY) * recentLocationMultiplier;

                    //work out the distance of the location from the recentLocationX/Y the distance it's ok to be from the recentLocationX/Y (works on time, with the ok distance increasing over time)
                    double disSqr = ((maxX - recentLocationX) * (maxX - recentLocationX) + (maxY - recentLocationY) * (maxY - recentLocationY));
                    double okDisSqr = timeSinceLastFind.TotalSeconds * timeSinceLastFind.TotalSeconds * okDistFromRecentLocationMultipier;

                    setText("\nrecentLocationX: " + recentLocationX + ", recentLocationY: " + recentLocationY + ", disSqr: " + disSqr + ", okDisSqr: " + okDisSqr + "\n");
                    if (disSqr <= okDisSqr) //only if the location is within the ok distance
                    {
                        lastFindTime = DateTime.Now;

                        foundCount++;

                        if (foundCount >= foundCountThresh) //have to "correctly" locate location threshold times in a row
                        {
                            //debug (draw located location on map and save it)
                            /*
                            rect = new Rectangle(new Point(maxX * tileSize, maxY * tileSize), new Size(imageCapture.Width, imageCapture.Height));
                            imageOutput.Draw(rect, new Bgr(Color.Red), 4);

                            imageOutput.Draw(maxScore.ToString(), ref font, new Point((maxX * tileSize) + 8, (maxY * tileSize) + imageCapture.Height - 8), new Bgr(Color.CornflowerBlue));

                            imageOutput.Save(fnameRoot + "out.png");
                            */

                            write(maxX + 4, maxY + 4); //write location in output file

                            setText("\nOK!\n");
                        }
                        else
                        {
                            setText("\nfound: " + foundCount + "\n");
                        }

                    }
                    else
                    {
                        foundCount = 0;
                        setText("\nFail\n");
                    }
                }
                else
                {
                    setText("\nSame as previous\n");
                }
            }


            //debug (outputs the tile data graphically to see with the human eye)
            /*
            Image<Gray, Byte> imageNew = new Image<Gray, Byte>(mapWidth, mapHeight);
            for (int y = 0; y < mapHeight / tileSize; y++)
            {
                for (int x = 0; x < mapWidth / tileSize; x++)
                {
                    for (int y2 = 0; y2 < tileSize; y2++)
                    {
                        for (int x2 = 0; x2 < tileSize; x2++)
                        {
                            if (dataMap[y, x] == 3)
                            {
                                imageNew.Data[y * tileSize + y2, x * tileSize + x2, 0] = 0;
                            }
                            else if (dataMap[y, x] == 2)
                            {
                                imageNew.Data[y * tileSize + y2, x * tileSize + x2, 0] = 80;
                            }
                            else if (dataMap[y, x] == 1)
                            {
                                imageNew.Data[y * tileSize + y2, x * tileSize + x2, 0] = 150;
                            }
                            else
                            {
                                imageNew.Data[y * tileSize + y2, x * tileSize + x2, 0] = 255;
                            }
                        }
                    }
                }
            }



            imageBox1.Image = imageNew;*/


            return true;
        }

        //get screen capture
        private void getScreen(string fname)
        {
            IntPtr hWnd = IntPtr.Zero;
            //get handle of stream window
            foreach (Process pList in Process.GetProcesses())
            {
                if (pList.MainWindowTitle.Contains(windowTitle))
                {
                    hWnd = pList.MainWindowHandle;
                    break;
                }
            }

            if (hWnd != IntPtr.Zero)
            {
                // capture this window and save it
                ScreenCapture.CaptureWindowToFile(hWnd, fname, ImageFormat.Bmp);
            }
            else
            {
                setText("\nNothing to capture\n");
            }
        }

        public Form1()
        {
            InitializeComponent();
        }

        //set recentLocaitonX/Y to inputed choice (in pixels on the map, not tiles - easier to type in with the map open in paint etc without having to divide coords by 16 each time)
        private void buttonSetLocation_Click(object sender, EventArgs e)
        {
            int x, y;
            if (Int32.TryParse(textBoxLocationX.Text, out x) && Int32.TryParse(textBoxLocationY.Text, out y))
            {
                recentLocationX = x / tileSize;
                recentLocationY = y / tileSize;

                lastFindTime = DateTime.Now;
            }
            else
            {
                MessageBox.Show("parse error");
            }
        }

        //thread that does all the work, started from the timer tick
        private void backgroundWorker1_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                getScreen(fnameScreen); //get screen capture first

                Image<Gray, Byte> imageCapture = new Image<Gray, Byte>(fnameScreen); //load in screen capture

                //crop and resize part of interest
                Rectangle rect = new Rectangle(new Point(twitchViewX, twitchViewY), new Size(twitchViewWidth, twitchViewHeight));
                imageCapture.ROI = rect;
                imageCapture = imageCapture.Resize(160, 144, INTER.CV_INTER_LINEAR);
                //imageCapture.Save(fnameRoot + "test.png");

                //process and find location of screen on map
                if (doWork(imageCapture)) //if played located in middle of screen
                {
                    setTimerInterval(findTimeInterval);
                }
                else
                {
                    setTimerInterval(captureTimeInterval);
                }

                imageCapture.Dispose(); //no need for capture anymore
            }
            catch (Exception ex) //just to make sure that the app doesn't crash while working here
            {
                setText("\n" + ex.Message + "\n");
            }
        }

        //outputs text to richTextBox
        delegate void setTextCallback(string text);
        private void setText(string text)
        {
            // InvokeRequired required compares the thread ID of the
            // calling thread to the thread ID of the creating thread.
            // If these threads are different, it returns true.
            if (richTextBox1.InvokeRequired)
            {
                setTextCallback d = new setTextCallback(setText);
                this.Invoke(d, new object[] { text });
            }
            else
            {
                outputLines++;
                if (outputLines % outputLinesThresh == 0)
                {
                    richTextBox1.Text = "";
                }

                richTextBox1.AppendText(text);
                richTextBox1.ScrollToCaret();
            }
        }

        //because the timer interval is set from another thread
        delegate void setTimerIntervalCallbacl(int time);
        private void setTimerInterval(int time)
        {

            if (richTextBox1.InvokeRequired)
            {
                setTimerIntervalCallbacl d = new setTimerIntervalCallbacl(setTimerInterval);
                this.Invoke(d, new object[] { time });
            }
            else
            {
                timer1.Interval = time;
            }
        }

        //set current stream time
        private void buttonTime_Click(object sender, EventArgs e)
        {
            TimeSpan span;
            if (!TimeSpan.TryParse(textBoxTime.Text, out span))
            {
                MessageBox.Show("parse error");
            }
            else
            {
                start = DateTime.Now - span;
            }
        }

        //output data to text file
        private void write(int x, int y)
        {
            TimeSpan span = DateTime.Now - start;
            long time = (long)span.TotalSeconds;

            File.AppendAllText(fnameRoot + fnameText, time + "," + x + "," + y + Environment.NewLine);
        }

        private void buttonGenerate_Click(object sender, EventArgs e)
        {
            backgroundWorkerGenerate.RunWorkerAsync();
        }

        //generate map from outputted data text file
        private void backgroundWorkerGenerate_DoWork(object sender, DoWorkEventArgs e)
        {
            string[] lines = File.ReadAllLines(fnameRoot + fnameText);

            Bitmap bitmap = new Bitmap(fnameMap); //load in map, this time using native c# stuff, as openCV (or emgu?) seems to suck with drawing shapes with alpha
            Graphics gr = Graphics.FromImage(bitmap);
            gr.CompositingQuality = CompositingQuality.GammaCorrected; //no idea if it's better to have this line existing or not
            Pen pen;

            int xLast = 0;
            int yLast = 0;
            bool first = true;

            double timeStageLength = (60 * 60 * colourLoopTimeHours) / 6.0f; //used to calculate colour (colour goes from re->blue->red which involves rgb going through 6 phases, which one loop is set to do in colourLoopTimeHours

            foreach (string line in lines)
            {
                string[] parts = line.Split(',');
                //part[0] = time, part[1] = x, part[2] = y;

                long time;
                if (Int64.TryParse(parts[0], out time))
                {
                    int x;
                    if (Int32.TryParse(parts[1], out x))
                    {
                        int y;
                        if (Int32.TryParse(parts[2], out y))
                        {
                            x = x * tileSize;
                            y = y * tileSize;

                            if (first) //x/yLast doesn't exist at first, so need to set it here
                            {
                                first = false;
                                xLast = x;
                                yLast = y;
                            }
                            else
                            {
                                //LineSegment2D l = new LineSegment2D(new Point(xLast, yLast), new Point(x, y)); //openCV way


                                double b, g, r;
                                r = 1;
                                g = 0;
                                b = 0;

                                time = (long)(time % (60 * 60 * colourLoopTimeHours)); //colour loops after a while, so make time loop to generate the colour from this looping time

                                int timeStage = (int)(time / timeStageLength); //at which stage (out of 6) the colour is going through
                                double timeStagePos = time % timeStageLength; //colour position in current stage
                                double timeStagePosDouble = ((double)timeStagePos / (double)timeStageLength) * 255; //colour value from 0 to 255
                                double timeStagePosDoubleRev = 255 - timeStagePosDouble; //opposite of above

                                //the 6 stages (u = up (increasing), d = down (decreasing))
                                /*
                                0 r1 gu b0
                                1 g1 rd b0
                                2 g1 bu r0
                                3 b1 gd r0
                                4 b1 ru g0
                                5 r1 bd g0
                                */
                                switch (timeStage)
                                {
                                    case 0:
                                        r = 255;
                                        g = timeStagePosDouble;
                                        b = 0;
                                        break;
                                    case 1:
                                        r = timeStagePosDoubleRev;
                                        g = 255;
                                        b = 0;
                                        break;
                                    case 2:
                                        r = 0;
                                        g = 255;
                                        b = timeStagePosDouble;
                                        break;
                                    case 3:
                                        r = 0;
                                        g = timeStagePosDoubleRev;
                                        b = 255;
                                        break;
                                    case 4:
                                        r = timeStagePosDouble;
                                        g = 0;
                                        b = 255;
                                        break;
                                    case 5:
                                        r = 255;
                                        g = 0;
                                        b = timeStagePosDoubleRev;
                                        break;
                                }


                                //Bgr colour = new Bgr(b, g, r); //openCV way
                                pen = new Pen(Color.FromArgb(penAlpha, (int)r, (int)g, (int)b), lineThickness);
                                pen.StartCap = LineCap.Round; //round line ends
                                pen.EndCap = LineCap.Round;
                                gr.DrawLine(pen, new Point(xLast, yLast), new Point(x, y));

                                //openCV ways
                                //imageOutput.Draw(l, colour, 8);
                                //imageOutput = imageOutput.AddWeighted(overlay, 0.01, 0.99, 0);

                                //overlay.Dispose();
                                //overlay = imageOutput.Copy();

                                xLast = x;
                                yLast = y;
                            }
                        }
                    }
                }
            }


            bitmap.Save(fnameRoot + "gen.png", ImageFormat.Png);

            bitmap.Dispose();
        }

        private void backgroundWorkerGenerate_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            setText("\nGENERATED\n");
        }

        private void buttonSetView_Click(object sender, EventArgs e)
        {
            setTwitchView();
        }

        //set coords of game in stream
        private bool setTwitchView()
        {
            int x, y, width, height;

            if (Int32.TryParse(textBoxTwitchLeft.Text, out x))
            {
                if (Int32.TryParse(textBoxTwitchTop.Text, out y))
                {
                    if (Int32.TryParse(textBoxTwitchWidth.Text, out width))
                    {
                        if (Int32.TryParse(textBoxTwitchHeight.Text, out height))
                        {
                            twitchViewX = x;
                            twitchViewY = y;
                            twitchViewWidth = width;
                            twitchViewHeight = height;
                            return true;
                        }
                    }
                }
            }

            MessageBox.Show("parse error");
            return false;
        }





    }
}
