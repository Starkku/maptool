﻿/*
 * Copyright 2017 by Starkku
 * This file is part of MapTool, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using CNCMaps.FileFormats.Encodings;
using CNCMaps.FileFormats.VirtualFileSystem;
using MapTool.Utility;

namespace MapTool
{
    class MapTool
    {

        // Tool initialized true/false.
        public bool Initialized
        {
            get;
            set;
        }

        // Map file altered true/false.
        public bool Altered
        {
            get;
            set;
        }

        private string fileInput;
        private string fileOutput;
        private int map_Width;
        private int map_Height;
        private int map_FullWidth;
        private int map_FullHeight;
        private string nl = Environment.NewLine;
        //private bool fixSectionOrder = false;

        INIFile mapConfig;                                                             // Map file.
        INIFile profileConfig;                                                         // Conversion profile INI file.
        INIFile theaterConfig;                                                         // Theater config INI file.
        List<MapTileContainer> isoMapPack5 = new List<MapTileContainer>();             // Map tile data.
        byte[] overlayPack = null;                                                     // Map overlay ID data.
        byte[] overlayDataPack = null;                                                 // Map overlay frame data.
        string mapTheater = null;                                                      // Map theater data.
        List<string> applicableTheaters = new List<string>();                          // Conversion profile applicable theaters.
        string newTheater = null;                                                      // Conversion profile new theater.
        List<ByteIDConversionRule> tilerules = new List<ByteIDConversionRule>();       // Conversion profile tile rules.
        List<ByteIDConversionRule> overlayrules = new List<ByteIDConversionRule>();    // Conversion profile overlay rules.
        List<StringIDConversionRule> objectrules = new List<StringIDConversionRule>(); // Conversion profile object rules.
        List<SectionConversionRule> sectionrules = new List<SectionConversionRule>();  // Conversion profile section rules.

        public MapTool(string infile, string outfile, string fileconfig = null, bool list = false)
        {
            Initialized = false;
            Altered = false;
            fileInput = infile;
            fileOutput = outfile;

            if (list && !String.IsNullOrEmpty(fileInput))
            {
                theaterConfig = new INIFile(fileInput);
                if (!theaterConfig.Initialized)
                {
                    Initialized = false;
                    return;
                }
            }

            else if (!String.IsNullOrEmpty(fileInput) && !String.IsNullOrEmpty(fileOutput))
            {

                Logger.Info("Reading map file '" + infile + "'.");
                mapConfig = new INIFile(infile);
                if (!mapConfig.Initialized)
                {
                    Initialized = false;
                    return;
                }
                string[] size = mapConfig.GetKey("Map", "Size", "").Split(',');
                map_FullWidth = int.Parse(size[2]);
                map_FullHeight = int.Parse(size[3]);
                Initialized = parseMapPack();
                mapTheater = mapConfig.GetKey("Map", "Theater", null);
                if (mapTheater != null) mapTheater = mapTheater.ToUpper();

                profileConfig = new INIFile(fileconfig);
                if (!profileConfig.Initialized) Initialized = false;
                else
                {
                    Logger.Info("Parsing conversion profile file.");

                    string IncludeFiles = profileConfig.GetKey("ProfileData", "IncludeFiles", null);
                    if (!String.IsNullOrEmpty(IncludeFiles))
                    {
                        string[] inc = IncludeFiles.Split(',');
                        string basedir = Path.GetDirectoryName(fileconfig);
                        foreach (string f in inc)
                        {
                            if (File.Exists(basedir + "\\" + f))
                            {
                                INIFile ic = new INIFile(basedir + "\\" + f);
                                if (!ic.Initialized) continue;
                                Logger.Info("Merging included file '" + f + "' to conversion profile.");
                                profileConfig.Merge(ic);
                            }
                        }
                    }
                    string[] tilerules = null;
                    string[] overlayrules = null;
                    string[] objectrules = null;
                    //string[] sectionrules = null;
                    if (profileConfig.SectionExists("TileRules")) tilerules = profileConfig.GetValues("TileRules");
                    if (profileConfig.SectionExists("OverlayRules")) overlayrules = profileConfig.GetValues("OverlayRules");
                    if (profileConfig.SectionExists("ObjectRules")) objectrules = profileConfig.GetValues("ObjectRules");
                    //if (profileConfig.SectionExists("SectionRules")) sectionrules = profileConfig.GetValues("SectionRules");
                    string[] tmp = null;
                    newTheater = profileConfig.GetKey("TheaterRules", "NewTheater", null);
                    try
                    {
                        tmp = profileConfig.GetKey("TheaterRules", "ApplicableTheaters", null).Split(',');
                    }
                    catch (Exception)
                    {
                    }
                    if (newTheater != null) newTheater = newTheater.ToUpper();
                    if (tmp != null)
                    {
                        for (int i = 0; i < tmp.Length; i++)
                        {
                            applicableTheaters.Add(tmp[i].Trim().ToUpper());
                        }
                    }
                    if (applicableTheaters.Count < 1)
                        applicableTheaters.AddRange(new string[] { "TEMPERATE", "SNOW", "URBAN", "DESERT", "LUNAR", "NEWURBAN" });

                    if (tilerules == null && overlayrules == null && newTheater == null)
                    {
                        Logger.Error("No conversion rules to apply in conversion profile file. Aborting.");
                        Initialized = false;
                        return;
                    }

                    parseConfigFile(tilerules, this.tilerules);
                    parseConfigFile(overlayrules, this.overlayrules);
                    parseConfigFile(objectrules, this.objectrules);
                    //parseConfigFile(sectionrules, this.sectionrules);
                }
            }
            Initialized = true;
        }

        public void ConvertTheaterData()
        {
            if (!Initialized || applicableTheaters == null || newTheater == null) return;
            Logger.Info("Attempting to modify theater data of the map file.");
            if (mapTheater != null && !applicableTheaters.Contains(mapTheater))
            {
                Logger.Warn("Map theater declaration does not match profile configuration. No modifications will be made to the theater data.");
                return;
            }
            if (newTheater != "" && isValidTheatreName(newTheater))
            {
                mapConfig.SetKey("Map", "Theater", newTheater);
                Logger.Info("Map theater declaration changed from '" + mapTheater + "' to '" + newTheater + "'.");
                Altered = true;
            }
        }

        public void ConvertTileData()
        {
            if (!Initialized || isoMapPack5.Count < 1 || tilerules == null || tilerules.Count < 1) return;
            else if (mapTheater != null && applicableTheaters != null && !applicableTheaters.Contains(mapTheater)) { Logger.Warn("Conversion profile not applicable to maps belonging to this theater. No alterations will be made to the tile data."); return; }
            Logger.Info("Attempting to modify tile data of the map file.");
            ApplyTileConversionRules();
        }

        private void ApplyTileConversionRules()
        {
            int cells = (map_Width * 2 - 1) * map_Height;
            int lzoPackSize = cells * 11 + 4;
            byte[] isoMapPack = new byte[lzoPackSize];
            int l, h;
            int i = 0;

            foreach (MapTileContainer t in isoMapPack5)
            {
                if (t.TileIndex < 0 || t.TileIndex == 65535) t.TileIndex = 0;
                foreach (ByteIDConversionRule r in tilerules)
                {
                    l = r.Original_Start;
                    h = r.Original_End;
                    if (t.X == 122 && t.Y == 26)
                        t.UData = 0;
                    if (t.TileIndex >= l && t.TileIndex <= h)
                    {
                        if (r.HeightOverride >= 0)
                        {
                            t.Level = (byte)Math.Min(r.HeightOverride, 14);
                        }
                        if (r.SubIdxOverride >= 0)
                        {
                            t.SubTileIndex = (byte)Math.Min(r.SubIdxOverride, 255);
                        }
                        if (r.New_End == r.New_Start)
                        {
                            Logger.Debug("Tile ID " + t.TileIndex + " at " + t.X + "," + t.Y + " changed to " + r.New_Start);
                            t.TileIndex = r.New_Start;
                            break;
                        }
                        else
                        {
                            Logger.Debug("Tile ID " + t.TileIndex + " at " + t.X + "," + t.Y + " changed to " + (r.New_Start + Math.Abs(l - t.TileIndex)));
                            t.TileIndex = (r.New_Start + Math.Abs(l - t.TileIndex));
                            break;
                        }
                    }
                }
                byte[] x = BitConverter.GetBytes(t.X);
                byte[] y = BitConverter.GetBytes(t.Y);
                byte[] tilei = BitConverter.GetBytes(t.TileIndex);
                isoMapPack[i] = x[0];
                isoMapPack[i + 1] = x[1];
                isoMapPack[i + 2] = y[0];
                isoMapPack[i + 3] = y[1];
                isoMapPack[i + 4] = tilei[0];
                isoMapPack[i + 5] = tilei[1];
                isoMapPack[i + 6] = tilei[2];
                isoMapPack[i + 7] = tilei[3];
                isoMapPack[i + 8] = t.SubTileIndex;
                isoMapPack[i + 9] = t.Level;
                isoMapPack[i + 10] = t.Level;
                i += 11;
            }
            byte[] lzo = Format5.Encode(isoMapPack, 5);
            string data = Convert.ToBase64String(lzo, Base64FormattingOptions.None);
            overrideBase64MapSection("IsoMapPack5", data);
            Altered = true;
        }

        private void parseConfigFile(string[] new_rules, List<ByteIDConversionRule> current_rules)
        {
            if (new_rules == null || new_rules.Length < 1 || current_rules == null) return;
            current_rules.Clear();
            bool pm1ranged = false;
            bool pm2ranged = false;
            int pm1val1 = 0;
            int pm1val2 = 0;
            int pm2val1 = 0;
            int pm2val2 = 0;
            foreach (string str in new_rules)
            {
                string[] st = str.Split('|');
                if (st.Length < 2) continue;
                if (st[0].Contains('-'))
                {
                    pm1ranged = true;
                    try
                    {
                        string[] st2 = st[0].Split('-');
                        pm1val1 = Convert.ToInt32(st2[0]);
                        pm1val2 = Convert.ToInt32(st2[1]);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
                else try
                    {
                        pm1val1 = Convert.ToInt32(st[0]);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                if (st[1].Contains('-'))
                {
                    pm2ranged = true;
                    try
                    {
                        string[] st2 = st[1].Split('-');
                        pm2val1 = Convert.ToInt32(st2[0]);
                        pm2val2 = Convert.ToInt32(st2[1]);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                }
                else try
                    {
                        pm2val1 = Convert.ToInt32(st[1]);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                int heightovr = -1;
                int subovr = -1;
                if (st.Length >= 3 && st[2] != null && !st[2].Equals("*", StringComparison.InvariantCultureIgnoreCase))
                {
                    heightovr = ParseInt(st[2], -1);
                }
                if (st.Length >= 4 && st[3] != null && !st[3].Equals("*", StringComparison.InvariantCultureIgnoreCase))
                {
                    subovr = ParseInt(st[3], -1);
                }

                if (pm1ranged && pm2ranged)
                {
                    current_rules.Add(new ByteIDConversionRule(pm1val1, pm2val1, pm1val2, pm2val2, heightovr, subovr));
                }
                else if (pm1ranged && !pm2ranged)
                {
                    int diff = pm2val1 + (pm1val2 - pm1val1);
                    current_rules.Add(new ByteIDConversionRule(pm1val1, pm2val1, pm1val2, diff, heightovr, subovr));
                }
                else
                {
                    current_rules.Add(new ByteIDConversionRule(pm1val1, pm2val1, -1, -1, heightovr, subovr));
                }
                pm1ranged = false;
                pm2ranged = false;
            }
        }

        private int ParseInt(string s, int defval)
        {
            try
            {
                return Int32.Parse(s);
            }
            catch (Exception)
            {
                return defval;
            }
        }

        private void parseConfigFile(string[] new_rules, List<StringIDConversionRule> current_rules)
        {
            if (new_rules == null || new_rules.Length < 1 || current_rules == null) return;
            current_rules.Clear();
            foreach (string str in new_rules)
            {
                string[] st = str.Split('|');
                if (st.Length == 1) current_rules.Add(new StringIDConversionRule(st[0], null));
                else if (st.Length >= 2) current_rules.Add(new StringIDConversionRule(st[0], st[1]));
            }
        }

        private void parseConfigFile(string[] new_rules, List<SectionConversionRule> current_rules)
        {
            if (new_rules == null || new_rules.Length < 1 || current_rules == null) return;
            current_rules.Clear();
            foreach (string str in new_rules)
            {
                string nid = null;
                int type = 0;
                string[] st = str.Split('|');
                if (st == null || st.Length < 1) continue;
                string id = st[0];
                if (id.StartsWith("+")) { type = 1; id = id.Substring(1, id.Length - 1); }
                else if (id.StartsWith("-")) { type = 2; id = id.Substring(1, id.Length - 1); }
                else if (id.StartsWith("*"))
                {
                    string[] ids = id.Split(',');
                    if (ids.Length > 1)
                    {
                        id = ids[0].Replace("*", "");
                        nid = ids[1];
                    }
                    else id = id.Replace("*", "");
                }
                List<SectionKVP> kvplist = new List<SectionKVP>();
                for (int i = 1; i < st.Length; i++)
                {
                    string[] kvp = st[i].Split('=');
                    int kvptype = 0;
                    if (kvp.Length < 1) continue;
                    string key = kvp[0];
                    string value = null;
                    if (kvp.Length == 2) value = kvp[1];
                    if (key.StartsWith("+")) { kvptype = 1; key = key.Substring(1, key.Length - 1); }
                    else if (key.StartsWith("-")) { kvptype = 2; key = key.Substring(1, key.Length - 1); }
                    kvplist.Add(new SectionKVP(key, value, (SectionRuleType)kvptype));
                }
                current_rules.Add(new SectionConversionRule(id, nid, kvplist, (SectionRuleType)type));
            }
        }

        private bool parseMapPack()
        {
            Logger.Info("Parsing IsoMapPack5.");
            string data = "";
            string[] tmp = mapConfig.GetValues("IsoMapPack5");
            if (tmp == null || tmp.Length < 1) return false;
            data = concatStrings(tmp);
            int cells;
            byte[] isoMapPack;
            try
            {
                string size = mapConfig.GetKey("Map", "Size", "");
                string[] st = size.Split(',');
                map_Width = Convert.ToInt16(st[2]);
                map_Height = Convert.ToInt16(st[3]);
                byte[] lzoData = Convert.FromBase64String(data);
                byte[] test = lzoData;
                cells = (map_Width * 2 - 1) * map_Height;
                int lzoPackSize = cells * 11 + 4;
                isoMapPack = new byte[lzoPackSize];
                uint total_decompress_size = Format5.DecodeInto(lzoData, isoMapPack);
            }
            catch (Exception)
            {
                return false;
            }
            MemoryFile mf = new MemoryFile(isoMapPack);
            for (int i = 0; i < cells; i++)
            {
                ushort rx = mf.ReadUInt16();
                ushort ry = mf.ReadUInt16();
                int tilenum = mf.ReadInt32();
                byte subtile = mf.ReadByte();
                byte level = mf.ReadByte();
                byte udata = mf.ReadByte();
                int dx = rx - ry + map_FullWidth - 1;
                int dy = rx + ry - map_FullWidth - 1;
                isoMapPack5.Add(new MapTileContainer((short)rx, (short)ry, tilenum, subtile, level, udata));
            }
            return true;
        }

        public void ConvertObjectData()
        {
            if (!Initialized || overlayrules == null || objectrules.Count < 1) return;
            else if (mapTheater != null && applicableTheaters != null && !applicableTheaters.Contains(mapTheater)) { Logger.Warn("Conversion profile not applicable to maps belonging to this theater. No alterations will be made to the object data."); return; }
            Logger.Info("Attempting to modify object data of the map file.");
            ApplyObjectConversionRules("Aircraft");
            ApplyObjectConversionRules("Units");
            ApplyObjectConversionRules("Infantry");
            ApplyObjectConversionRules("Structures");
        }

        private void ApplyObjectConversionRules(string sectionname)
        {
            if (String.IsNullOrEmpty(sectionname)) return;
            KeyValuePair<string, string>[] kvps = mapConfig.GetKeyValuePairs(sectionname);
            if (kvps == null) return;
            foreach (KeyValuePair<string, string> kvp in kvps)
            {
                foreach (StringIDConversionRule rule in objectrules)
                {
                    if (rule == null || rule.Original == null) continue;
                    if (kvp.Value.Contains(rule.Original))
                    {
                        if (rule.New == null)
                        {
                            Logger.Debug("Removed " + sectionname + " object with ID '" + rule.Original + "' from the map file.");
                            mapConfig.RemoveKey(sectionname, kvp.Key);
                            Altered = true;
                        }
                        else
                        {
                            Logger.Debug("Replaced " + sectionname + " object with ID '" + rule.Original + "' with object of ID '" + rule.New + "'.");
                            mapConfig.SetKey(sectionname, kvp.Key, kvp.Value.Replace("," + rule.Original + ",", "," + rule.New + ","));
                            Altered = true;
                        }
                    }
                }
            }
        }

        public void ConvertSectionData()
        {
            /*
            if (!Initialized || overlayrules == null || sectionrules.Count < 1) return;
            else if (mapTheater != null && applicableTheaters != null && !applicableTheaters.Contains(mapTheater)) { Logger.Warn("Conversion profile not applicable to maps belonging to this theater. No alterations will be made to the section data."); return; }
            Logger.Info("Attempting to modify section data of the map file.");
            ApplySectionConversionRules();
            fixSectionOrder = true;
            */
        }

        private void ApplySectionConversionRules()
        {
            /*
            foreach (SectionConversionRule rule in sectionrules)
            {
                IConfig section = mapConfig.Configs[rule.SectionID];
                if (section != null && rule.Type == SectionRuleType.Remove) { Logger.Debug("Removed section '" + section.Name + "'."); mapConfig.Configs.Remove(section); Altered = true; continue; }
                else if (section != null && rule.Type == SectionRuleType.Add) { Logger.Debug("Added section '" + rule.SectionID + "'."); mapConfig.Configs.Add(rule.SectionID); Altered = true; section = mapConfig.Configs[rule.SectionID]; }
                else if (section == null) continue;
                foreach (SectionKVP kvp in rule.KVPList)
                {
                    if (kvp.Type == SectionRuleType.Remove) { Logger.Debug("Removed key '" + kvp.Key + "' from section '" + section.Name + "."); section.Remove(kvp.Key); Altered = true; continue; }
                    else { section.Set(kvp.Key, kvp.Value); Logger.Debug("Set section '" + section.Name + "' key '" + kvp.Key + "' value to '" + kvp.Value + "'."); Altered = true; continue; }
                }
            }
            */
        }

        public void ConvertOverlayData()
        {
            if (!Initialized || overlayrules == null || overlayrules.Count < 1) return;
            else if (mapTheater != null && applicableTheaters != null && !applicableTheaters.Contains(mapTheater)) { Logger.Warn("Conversion profile not applicable to maps belonging to this theater. No alterations will be made to the overlay data."); return; }

            parseOverlayPack();

            Logger.Info("Attempting to modify overlay data of the map file.");
            ApplyOverlayConversionRules();
        }

        private void ApplyOverlayConversionRules()
        {
            for (int i = 0; i < Math.Min(overlayPack.Length, overlayDataPack.Length); i++)
            {
                if (overlayPack[i] == 255) continue;
                if (overlayPack[i] < 0 || overlayPack[i] > 255) overlayPack[i] = 0;
                if (overlayDataPack[i] < 0 || overlayDataPack[i] > 255) overlayDataPack[i] = 0;
                foreach (ByteIDConversionRule rule in overlayrules)
                {
                    if (!rule.ValidForOverlays()) continue;
                    if (overlayPack[i] >= rule.Original_Start && overlayPack[i] <= rule.Original_End)
                    {
                        if (rule.New_End == rule.New_Start)
                        {
                            Logger.Debug("Overlay ID '" + overlayPack[i] + " at array slot " + i + "' changed to '" + rule.New_Start + "'.");
                            overlayPack[i] = (byte)rule.New_Start;
                            break;
                        }
                        else
                        {
                            Logger.Debug("Overlay ID '" + overlayPack[i] + " at array slot " + i + "' changed to '" + (rule.New_Start + Math.Abs(rule.Original_Start - overlayPack[i])) + "'.");
                            overlayPack[i] = (byte)(rule.New_Start + Math.Abs(rule.Original_Start - overlayPack[i]));
                            break;
                        }
                    }
                }
            }
            saveOverlayPack();
        }

        private void parseOverlayPack()
        {
            Logger.Info("Parsing OverlayPack.");
            string[] vals = mapConfig.GetValues("OverlayPack");
            if (vals == null || vals.Length < 1) return;
            byte[] format80Data = Convert.FromBase64String(concatStrings(vals));
            var overlayPack = new byte[1 << 18];
            Format5.DecodeInto(format80Data, overlayPack, 80);

            Logger.Info("Parsing OverlayDataPack.");
            vals = mapConfig.GetValues("OverlayDataPack");
            if (vals == null || vals.Length < 1) return;
            format80Data = Convert.FromBase64String(concatStrings(vals));
            var overlayDataPack = new byte[1 << 18];
            Format5.DecodeInto(format80Data, overlayDataPack, 80);

            this.overlayPack = overlayPack;
            this.overlayDataPack = overlayDataPack;
        }

        private void saveOverlayPack()
        {
            string base64_overlayPack = Convert.ToBase64String(Format5.Encode(overlayPack, 80), Base64FormattingOptions.None);
            string base64_overlayDataPack = Convert.ToBase64String(Format5.Encode(overlayDataPack, 80), Base64FormattingOptions.None);
            overrideBase64MapSection("OverlayPack", base64_overlayPack);
            overrideBase64MapSection("OverlayDataPack", base64_overlayDataPack);
            Altered = true;
        }

        private void overrideBase64MapSection(string sectionName, string data)
        {
            //mapConfig.RemoveSection(sectionName);
            //mapConfig.AddSection(sectionName);
            int lx = 70;
            //int rownum = 1;
            List<string> lines = new List<string>();
            for (int x = 0; x < data.Length; x += lx)
            {
                lines.Add(data.Substring(x, Math.Min(lx, data.Length - x)));
                //mapConfig.SetKey(sectionName, rownum++.ToString(CultureInfo.InvariantCulture), data.Substring(x, Math.Min(lx, data.Length - x)));
            }
            mapConfig.ReplaceSectionValues(sectionName, lines);
        }

        private string concatStrings(string[] Strings)
        {
            if (Strings == null || Strings.Length < 1) return null;
            var sb = new StringBuilder();
            foreach (string v in Strings)
                sb.Append(v);
            return sb.ToString();
        }


        public void ListTileSetData()
        {
            if (theaterConfig == null) return;

            TilesetCollection mtiles = TilesetCollection.ParseFromINIFile(theaterConfig);

            if (mtiles == null || mtiles.Count < 1) { Logger.Error("Could not parse tileset data from theater configuration file '" + theaterConfig.Filename + "'."); return; };

            Logger.Info("Attempting to list tileset data for a theater based on file: '" + theaterConfig.Filename + "'.");
            List<string> lines = new List<string>();
            int tilecounter = 0;
            lines.Add("Theater tileset data gathered from file '" + theaterConfig.Filename + "'.");
            lines.Add("");
            lines.Add("");
            foreach (Tileset ts in mtiles)
            {
                if (ts.TilesInSet < 1)
                {
                    Logger.Debug(ts.SetID + " (" + ts.SetName + ")" + " skipped due to tile count of 0.");
                    continue;
                }
                lines.AddRange(ts.getPrintableData(ref tilecounter));
                lines.Add("");
                Logger.Debug(ts.SetID + " (" + ts.SetName + ")" + " added to the list.");
            }
            File.WriteAllLines(fileOutput, lines.ToArray());
        }


        public void Save()
        {
            mapConfig.Save(fileOutput);
        }

        public static bool isValidTheatreName(string tname)
        {
            if (tname == "TEMPERATE" || tname == "SNOW" || tname == "LUNAR" || tname == "DESERT" || tname == "URBAN" || tname == "NEWURBAN") return true;
            return false;
        }

        public static short shortFromBytes(byte b1, byte b2)
        {
            byte[] b = new byte[] { b2, b1 };
            return BitConverter.ToInt16(b, 0);
        }

        public static int IntFromBytes(byte b1, byte b2, byte b3, byte b4)
        {
            byte[] b = new byte[] { b1, b2, b3, b4 };
            return BitConverter.ToInt32(b, 0);
        }

    }
}
