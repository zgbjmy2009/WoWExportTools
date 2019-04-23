﻿using OpenTK;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using WoWFormatLib.FileReaders;
using CASCLib;

namespace OBJExporterUI.Exporters.OBJ
{
    public class ADTExporter
    {
        public static void exportADT(string file, BackgroundWorker exportworker = null)
        {
            if (exportworker == null)
            {
                exportworker = new BackgroundWorker();
                exportworker.WorkerReportsProgress = true;
            }

            var outdir = ConfigurationManager.AppSettings["outdir"];

            var customCulture = (System.Globalization.CultureInfo)System.Threading.Thread.CurrentThread.CurrentCulture.Clone();
            customCulture.NumberFormat.NumberDecimalSeparator = ".";
            System.Threading.Thread.CurrentThread.CurrentCulture = customCulture;

            var MaxSize = 51200 / 3.0;
            var TileSize = MaxSize / 32.0;
            var ChunkSize = TileSize / 16.0;
            var UnitSize = ChunkSize / 8.0;
            var UnitSizeHalf = UnitSize / 2.0;

            Logger.WriteLine("ADT OBJ Exporter: Starting export of {0}..", file);

            if (!Directory.Exists(Path.Combine(outdir, Path.GetDirectoryName(file))))
            {
                Directory.CreateDirectory(Path.Combine(outdir, Path.GetDirectoryName(file)));
            }

            exportworker.ReportProgress(0, "Loading ADT " + file);

            var reader = new ADTReader();
            reader.LoadADT(file.Replace('/', '\\'));

            if (reader.adtfile.chunks == null)
            {
                Logger.WriteLine("ADT OBJ Exporter: File {0} has no chunks, skipping export!", file);
                return;
            }

            var renderBatches = new List<Structs.RenderBatch>();
            var verticelist = new List<Structs.Vertex>();
            var indicelist = new List<int>();
            var materials = new Dictionary<int, string>();

            ConfigurationManager.RefreshSection("appSettings");
            var bakeQuality = ConfigurationManager.AppSettings["bakeQuality"];

            // Calculate ADT offset in world coordinates
            var adtStartX = ((reader.adtfile.x - 32) * TileSize) * -1;
            var adtStartY = ((reader.adtfile.y - 32) * TileSize) * -1;

            // Calculate first chunk offset in world coordinates
            var initialChunkX = adtStartY + (reader.adtfile.chunks[0].header.indexX * ChunkSize) * -1;
            var initialChunkY = adtStartX + (reader.adtfile.chunks[0].header.indexY * ChunkSize) * -1;

            uint ci = 0;
            for(var x = 0; x < 16; x++)
            {
                for (var y = 0; y < 16; y++)
                {
                    var genx = (initialChunkX + (ChunkSize * x) * -1);
                    var geny = (initialChunkY + (ChunkSize * y) * -1);

                    Console.WriteLine(ci + ": " + x + "." + y + ": Gen X " + genx.ToString("G") + ", Gen Y " + geny.ToString("G") + ", Orig X " + reader.adtfile.chunks[ci].header.position.X + ", Orig Y " + reader.adtfile.chunks[ci].header.position.Y);

                    var chunk = reader.adtfile.chunks[ci];

                    var off = verticelist.Count();

                    var batch = new Structs.RenderBatch();

                    for (int i = 0, idx = 0; i < 17; i++)
                    {
                        for (var j = 0; j < (((i % 2) != 0) ? 8 : 9); j++)
                        {
                            var v = new Structs.Vertex();

                            v.Normal = new Structs.Vector3D
                            {
                                X = (double)chunk.normals.normal_2[idx] / 127,
                                Y = (double)chunk.normals.normal_0[idx] / 127,
                                Z = (double)chunk.normals.normal_1[idx] / 127
                            };

                            var px = geny - (j * UnitSize);
                            var py = chunk.vertices.vertices[idx++] + chunk.header.position.Z;
                            var pz = genx - (i * UnitSizeHalf);

                            v.Position = new Structs.Vector3D
                            {
                                X = px,
                                Y = py,
                                Z = pz
                            };

                            if ((i % 2) != 0) v.Position.X = (px - UnitSizeHalf);

                            if (bakeQuality == "low" || bakeQuality == "medium")
                            {
                                var tx = -(v.Position.X - initialChunkY) / TileSize;
                                var ty = -(v.Position.Z - initialChunkX) / TileSize;
                                v.TexCoord = new Structs.Vector2D { X = tx, Y = ty };
                            }
                            else if (bakeQuality == "high")
                            {
                                // Multiple textures per model, one per chunk
                                var tx = -(v.Position.X) / ChunkSize;
                                var ty = -(v.Position.Z) / ChunkSize;
                                v.TexCoord = new Structs.Vector2D { X = tx, Y = ty };
                            }
                            verticelist.Add(v);
                        }
                    }

                    batch.firstFace = (uint)indicelist.Count();

                    // Stupid C# and its structs
                    var holesHighRes = new byte[8];
                    holesHighRes[0] = chunk.header.holesHighRes_0;
                    holesHighRes[1] = chunk.header.holesHighRes_1;
                    holesHighRes[2] = chunk.header.holesHighRes_2;
                    holesHighRes[3] = chunk.header.holesHighRes_3;
                    holesHighRes[4] = chunk.header.holesHighRes_4;
                    holesHighRes[5] = chunk.header.holesHighRes_5;
                    holesHighRes[6] = chunk.header.holesHighRes_6;
                    holesHighRes[7] = chunk.header.holesHighRes_7;

                    for (int j = 9, xx = 0, yy = 0; j < 145; j++, xx++)
                    {
                        if (xx >= 8) { xx = 0; ++yy; }
                        var isHole = true;

                        // Check if chunk is using low-res holes
                        if ((chunk.header.flags & 0x10000) == 0)
                        {
                            // Calculate current hole number
                            var currentHole = (int)Math.Pow(2,
                                    Math.Floor(xx / 2f) * 1f +
                                    Math.Floor(yy / 2f) * 4f);

                            // Check if current hole number should be a hole
                            if ((chunk.header.holesLowRes & currentHole) == 0)
                            {
                                isHole = false;
                            }
                        }

                        else
                        {
                            // Check if current section is a hole
                            if (((holesHighRes[yy] >> xx) & 1) == 0)
                            {
                                isHole = false;
                            }
                        }

                        if (!isHole)
                        {
                            indicelist.AddRange(new int[] { off + j + 8, off + j - 9, off + j });
                            indicelist.AddRange(new int[] { off + j - 9, off + j - 8, off + j });
                            indicelist.AddRange(new int[] { off + j - 8, off + j + 9, off + j });
                            indicelist.AddRange(new int[] { off + j + 9, off + j + 8, off + j });

                            // Generates quads instead of 4x triangles
                            //indicelist.AddRange(new int[] { off + j + 8, off + j - 9, off + j - 8 });
                            //indicelist.AddRange(new int[] { off + j - 8, off + j + 9, off + j + 8 });

                        }

                        if ((j + 1) % (9 + 8) == 0) j += 9;
                    }

                    if (bakeQuality == "low" || bakeQuality == "medium")
                    {
                        if (!materials.ContainsKey(1))
                        {
                            materials.Add(1, Path.GetFileNameWithoutExtension(file));
                        }
                        batch.materialID = (uint)materials.Count();
                    }
                    else if (bakeQuality == "high")
                    {
                        materials.Add((int)ci + 1, Path.GetFileNameWithoutExtension(file) + "_" + ci);
                        batch.materialID = ci + 1;
                    }

                    batch.numFaces = (uint)(indicelist.Count()) - batch.firstFace;

                    var layermats = new List<uint>();


                    renderBatches.Add(batch);
                    ci++;
                }
            }

            ConfigurationManager.RefreshSection("appSettings");

            if (ConfigurationManager.AppSettings["exportWMO"] == "True" || ConfigurationManager.AppSettings["exportM2"] == "True")
            {
                var doodadSW = new StreamWriter(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file).Replace(" ", "") + "_ModelPlacementInformation.csv"));
                doodadSW.WriteLine("ModelFile;PositionX;PositionY;PositionZ;RotationX;RotationY;RotationZ;ScaleFactor;ModelId;Type");

                if (ConfigurationManager.AppSettings["exportWMO"] == "True")
                {
                    exportworker.ReportProgress(25, "Exporting WMOs");

                    for (var mi = 0; mi < reader.adtfile.objects.worldModels.entries.Count(); mi++)
                    {
                        var wmo = reader.adtfile.objects.worldModels.entries[mi];

                        var filename = "";
                        uint filedataid = 0;

                        if (reader.adtfile.objects.wmoNames.filenames == null)
                        {
                            filedataid = wmo.mwidEntry;
                            var lookup = WoWFormatLib.Utils.CASC.getHashByFileDataID(filedataid);
                            MainWindow.filenameLookup.TryGetValue(lookup, out filename);
                        }
                        else
                        {
                            Logger.WriteLine("Warning!! File " + filename + " ID: " + filedataid + " still has filenames!");
                            filename = reader.adtfile.objects.wmoNames.filenames[wmo.mwidEntry];
                            filedataid = WoWFormatLib.Utils.CASC.getFileDataIdByName(filename);
                        }

                        if (string.IsNullOrEmpty(filename))
                        {
                            if (!File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), filedataid.ToString() + ".obj")))
                            {
                                WMOExporter.exportWMO(filedataid, exportworker, Path.Combine(outdir, Path.GetDirectoryName(file)), wmo.doodadSet);
                            }

                            if (File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), filedataid.ToString() + ".obj")))
                            {
                                doodadSW.WriteLine(filedataid + ".obj;" + wmo.position.X + ";" + wmo.position.Y + ";" + wmo.position.Z + ";" + wmo.rotation.X + ";" + wmo.rotation.Y + ";" + wmo.rotation.Z + ";" + wmo.scale / 1024f + ";" + wmo.uniqueId + ";wmo");
                            }
                        }
                        else
                        {
                            if (!File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj")))
                            {
                                WMOExporter.exportWMO(filedataid, exportworker, Path.Combine(outdir, Path.GetDirectoryName(file)), wmo.doodadSet, filename);
                            }

                            if (File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj")))
                            {
                                doodadSW.WriteLine(Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj;" + wmo.position.X + ";" + wmo.position.Y + ";" + wmo.position.Z + ";" + wmo.rotation.X + ";" + wmo.rotation.Y + ";" + wmo.rotation.Z + ";" + wmo.scale / 1024f + ";" + wmo.uniqueId + ";wmo");
                            }
                        }
                    }
                }

                if (ConfigurationManager.AppSettings["exportM2"] == "True")
                {
                    exportworker.ReportProgress(50, "Exporting M2s");

                    for (var mi = 0; mi < reader.adtfile.objects.models.entries.Count(); mi++)
                    {
                        var doodad = reader.adtfile.objects.models.entries[mi];

                        var filename = "";
                        uint filedataid = 0;

                        if (reader.adtfile.objects.wmoNames.filenames == null)
                        {
                            filedataid = doodad.mmidEntry;
                            var lookup = WoWFormatLib.Utils.CASC.getHashByFileDataID(filedataid);
                            MainWindow.filenameLookup.TryGetValue(lookup, out filename);
                        }
                        else
                        {
                            filename = reader.adtfile.objects.wmoNames.filenames[doodad.mmidEntry];
                            filedataid = WoWFormatLib.Utils.CASC.getFileDataIdByName(filename);
                        }

                        if (!File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj")))
                        {
                            M2Exporter.ExportM2(filedataid, null, Path.Combine(outdir, Path.GetDirectoryName(file)), filename);
                        }

                        if (File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj")))
                        {
                            doodadSW.WriteLine(Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj;" + doodad.position.X + ";" + doodad.position.Y + ";" + doodad.position.Z + ";" + doodad.rotation.X + ";" + doodad.rotation.Y + ";" + doodad.rotation.Z + ";" + doodad.scale / 1024f + ";" + doodad.uniqueId + ";m2");
                        }
                    }

                    exportworker.ReportProgress(65, "Exporting foliage");

                    try
                    {
                        if (!File.Exists("definitions/GroundEffectTexture.dbd") || !File.Exists("definitions/GroundEffectDoodad.dbd"))
                        {
                            MainWindow.UpdateDefinition("GroundEffectTexture");
                            MainWindow.UpdateDefinition("GroundEffectDoodad");
                            DefinitionManager.LoadDefinitions();
                        }

                        var build = WoWFormatLib.Utils.CASC.BuildName;
                        var groundEffectTextureDB = DBCManager.LoadDBC("GroundEffectTexture", build, true);
                        var groundEffectDoodadDB = DBCManager.LoadDBC("GroundEffectDoodad", build, true);

                        for (var c = 0; c < reader.adtfile.texChunks.Length; c++)
                        {
                            for (var l = 0; l < reader.adtfile.texChunks[c].layers.Length; l++)
                            {
                                var effectID = reader.adtfile.texChunks[c].layers[l].effectId;
                                if (effectID == 0)
                                    continue;

                                if (!groundEffectTextureDB.Contains(effectID))
                                {
                                    Console.WriteLine("Could not find groundEffectTexture entry " + reader.adtfile.texChunks[c].layers[l].effectId);
                                    continue;
                                }

                                dynamic textureEntry = groundEffectTextureDB[effectID];
                                foreach (int doodad in textureEntry.DoodadID)
                                {
                                    if (!groundEffectDoodadDB.Contains(doodad))
                                    {
                                        Console.WriteLine("Could not find groundEffectDoodad entry " + doodad);
                                        continue;
                                    }

                                    dynamic doodadEntry = groundEffectDoodadDB[doodad];

                                    var filedataid = (uint)doodadEntry.ModelFileID;

                                    var lookup = WoWFormatLib.Utils.CASC.getHashByFileDataID(filedataid);
                                    MainWindow.filenameLookup.TryGetValue(lookup, out var filename);

                                    if (!File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), "foliage")))
                                    {
                                        Directory.CreateDirectory(Path.Combine(outdir, Path.GetDirectoryName(file), "foliage"));
                                    }

                                    if (!File.Exists(Path.Combine(outdir, Path.GetDirectoryName(file), "foliage", Path.GetFileNameWithoutExtension(filename).ToLower() + ".obj")))
                                    {
                                        M2Exporter.ExportM2(filedataid, null, Path.Combine(outdir, Path.GetDirectoryName(file), "foliage"), filename);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
#if DEBUG
                        throw e;
#else
                        Logger.WriteLine("An error occured while exporting foliage: " + e.Message);
#endif
                    }
                }

                doodadSW.Close();
            }

            exportworker.ReportProgress(75, "Exporting terrain textures..");

            if(bakeQuality != "none")
            {
                var mtlsw = new StreamWriter(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file).Replace(" ", "") + ".mtl"));

                //No idea how MTL files really work yet. Needs more investigation.
                foreach (var material in materials)
                {
                    mtlsw.WriteLine("newmtl " + material.Value.Replace(" ", ""));
                    mtlsw.WriteLine("Ka 1.000000 1.000000 1.000000");
                    mtlsw.WriteLine("Kd 0.640000 0.640000 0.640000");
                    mtlsw.WriteLine("map_Ka " + material.Value.Replace(" ", "") + ".png");
                    mtlsw.WriteLine("map_Kd " + material.Value.Replace(" ", "") + ".png");
                }

                mtlsw.Close();
            }

            exportworker.ReportProgress(85, "Exporting terrain geometry..");

            var indices = indicelist.ToArray();

            var adtname = Path.GetFileNameWithoutExtension(file);

            var objsw = new StreamWriter(Path.Combine(outdir, Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file).Replace(" ", "") + ".obj"));

            objsw.WriteLine("# Written by Marlamin's WoW OBJExporter. Original file: " + file);
            if (bakeQuality != "none")
            {
                objsw.WriteLine("mtllib " + Path.GetFileNameWithoutExtension(file).Replace(" ", "") + ".mtl");
            }

            objsw.WriteLine("g " + adtname.Replace(" ", ""));
            var verticeCounter = 1;
            var chunkCounter =1;
            foreach (var vertex in verticelist)
            {
                objsw.WriteLine("# C" + chunkCounter + ".V" + verticeCounter);
                objsw.WriteLine("v " + vertex.Position.X.ToString("R") + " " + vertex.Position.Y.ToString("R") + " " + vertex.Position.Z.ToString("R"));
                objsw.WriteLine("vt " + vertex.TexCoord.X + " " + -vertex.TexCoord.Y);
                objsw.WriteLine("vn " + vertex.Normal.X.ToString("R") + " " + vertex.Normal.Y.ToString("R") + " " + vertex.Normal.Z.ToString("R"));
                verticeCounter++;
                if(verticeCounter == 146)
                {
                    chunkCounter++;
                    verticeCounter = 1;
                }
            }

            if(bakeQuality == "low" || bakeQuality == "medium")
            {
                objsw.WriteLine("usemtl " + materials[1]);
                objsw.WriteLine("s 1");
            }

            foreach (var renderBatch in renderBatches)
            {
                var i = renderBatch.firstFace;
                if (bakeQuality == "high" && materials.ContainsKey((int)renderBatch.materialID)) { objsw.WriteLine("usemtl " + materials[(int)renderBatch.materialID]);  }
                while (i < (renderBatch.firstFace + renderBatch.numFaces))
                {
                    objsw.WriteLine("f " + 
                        (indices[i + 2] + 1) + "/" + (indices[i + 2] + 1) + "/" + (indices[i + 2] + 1) + " " + 
                        (indices[i + 1] + 1) + "/" + (indices[i + 1] + 1) + "/" + (indices[i + 1] + 1) + " " + 
                        (indices[i] + 1) + "/" + (indices[i] + 1) + "/" + (indices[i] + 1));
                    i = i + 3;
                }
            }

            objsw.Close();
        }
    }
}
