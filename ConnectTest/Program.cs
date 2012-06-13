﻿using System;
using System.Net;
using Hyperletter;

namespace ConnectTest
{
    class Program
    {
        public static object SyncRoot = new object();
        static void Main(string[] args) {
            var hs = new HyperSocket(SocketMode.Unicast);
            //hs.Received += letter => Console.WriteLine(DateTime.Now + " ACTUALY RECEIVED: " + letter.Parts[0].Data);
            int y = 0;
            hs.Sent += letter => {
                lock (SyncRoot) {
                    y++;
                    //if (y%500 == 0) {
                        Console.WriteLine("->" + y);
                    //}
                }
            };
            int z = 0;
            hs.Received += letter => {
                z++;
                if(z % 500 == 0)
                    Console.WriteLine("<-" + z);
            };
            hs.Discarded += hs_Discarded;
            hs.Requeued += letter => Console.WriteLine("REQUEUED");

            hs.Connect(IPAddress.Parse("127.0.0.1"), 8001);
            hs.Connect(IPAddress.Parse("127.0.0.1"), 8002);
            string line;
            while ((line = Console.ReadLine()) != null) {
                if (line == "exit")
                    return;
                if(line == "s")
                    Console.WriteLine(y);
                else 
                    for (int i = 0; i < 10; i++ )
                        hs.Send(new Letter() { Options = LetterOptions.NoAck, LetterType = LetterType.User, Parts = new IPart[] { new Part { PartType = PartType.User, Data = new[] { (byte)'A' } } } });
            }
        }

        static void hs_Discarded(Binding arg1, ILetter arg2) {
            Console.WriteLine(arg1 + " " + arg2);
        }
    }
}
