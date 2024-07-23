using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DelaunatorSharp;
using DelaunatorSharp.Unity.Extensions;
// using DelaunatorSharp.Unity.Extensions;
using Sirenix.Utilities;
using UnityEditor;
using UnityEngine;

namespace Moonflow.MFAssetTools.MFTransparencyConvexBuilder
{
    
    public class MFQuadParticleMeshOptimizer : EditorWindow
    {
        public Texture2D tex;
        public bool sequenceMode;
        public Vector2Int gridNum = Vector2Int.one;
        public int cuttingMode;
        private static readonly string[] cuttingModeText = new[] { "裁黑边", "裁透明", "容差裁透明" };
        public float tolerance;
        public int previewIndex;
        public bool showOutline;

        private Vector2Int wh;
        private Vector2Int singleCellSize;
        private HashSet<Vector2Int> _edgepoint;
        private Mesh _mesh;
        
        private GameObject _previewObject;
        private MeshFilter _previewMeshFilter;
        private MeshRenderer _previewMeshRenderer;
        private Material _previewMat;

        private Delaunator delaunator;
        
        private static MFQuadParticleMeshOptimizer _ins;
        private static string PREVIEW_SHADER_NAME = "Hidden/Moonflow/QuadParticlePreview";
        private static string PREVIEW_SHADER_TEX_PROP_NAME = "_MainTex";
        private static string PREVIEW_SHADER_TEX_ST_PROP_NAME = "_MainTex_ST";
        
        [MenuItem("Tools/Moonflow/Tools/Assets/MFQuadParticleMeshOptimizer #%V")]
        private static void ShowWindow()
        {
            _ins = GetWindow<MFQuadParticleMeshOptimizer>();
            _ins.Show();
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            tex = EditorGUILayout.ObjectField("贴图", tex, typeof(Texture2D), false) as Texture2D;
            sequenceMode = EditorGUILayout.ToggleLeft("序列帧模式", sequenceMode);
            if (sequenceMode)
            {
                EditorGUI.indentLevel++;
                gridNum = EditorGUILayout.Vector2IntField("序列帧行列", gridNum);
                previewIndex = EditorGUILayout.IntSlider("预览序号", previewIndex, 0, gridNum.x * gridNum.y - 1);
                EditorGUI.indentLevel--;
            }
            tolerance = EditorGUILayout.Slider("容差", tolerance, 0f, 0.999f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("生成"))
                {
                    _mesh = Tex2Mesh();
                }

                if (GUILayout.Button("预览"))
                {
                    // if (_mesh == null)
                    Clear();
                    _mesh = Tex2Mesh();
                    // if(_mesh!=null)
                    CreatePreview(_mesh);
                }

                if (GUILayout.Button("清除"))
                {
                    Clear();
                }
            }

            if (GUILayout.Button("切换轮廓显示"))
            {
                showOutline = !showOutline;
                var asm = Assembly.GetAssembly(typeof(Editor));
                var type = asm.GetType("UnityEditor.AnnotationUtility");

                if (type == null)
                {
                    return;
                }

                var setSelectionWire = type.GetProperty("showSelectionOutline", BindingFlags.Static | BindingFlags.NonPublic);
                if (setSelectionWire == null)
                    return;
                setSelectionWire.SetValue(null, showOutline, null);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("同名保存到贴图路径"))
                {
                    var path = AssetDatabase.GetAssetPath(tex);
                    path = path.Replace(Path.GetExtension(path), ".asset");
                    AssetDatabase.CreateAsset(_mesh, path);
                    AssetDatabase.Refresh();
                }

                if (GUILayout.Button("保存到指定路径"))
                {
                    var path = EditorUtility.SaveFilePanel("保存到", Application.dataPath, tex.name, "asset");
                    AssetDatabase.CreateAsset(_mesh, "Assets" + path.Substring(Application.dataPath.Length));
                    AssetDatabase.Refresh();
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                if(_previewMat!=null)
                    _previewMat.SetVector(PREVIEW_SHADER_TEX_ST_PROP_NAME, GetIndexOffset(previewIndex));
            }
        }

        private Vector4 GetIndexOffset(int index)
        {
            // return new Vector4(1, 1, 0, 0);
            //序号起始左上，xy起始左下，y从1向0排
            Vector4 st = new Vector4(1.0f / gridNum.x, 1.0f / gridNum.y, 0, 0);
            st.z = index - (index / gridNum.x) * gridNum.x;
            st.w = (index - st.z) / gridNum.x + 1;
            st.z = st.z / gridNum.x;
            st.w = 1.0f - st.w / gridNum.y;
            return st;
        }

        private void OnDestroy()
        {
            Clear();
        }

        public Mesh Tex2Mesh()
        {
            if (tex == null) return null;
            bool originalReadable = tex.isReadable;
            if (!originalReadable)
            {
                var pathToTex = AssetDatabase.GetAssetPath(tex);
                TextureImporter importer = TextureImporter.GetAtPath(pathToTex) as TextureImporter;
                importer.isReadable = true;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            wh.x = tex.width;
            wh.y = tex.height;
            _edgepoint = new HashSet<Vector2Int>();
            AddPoints(wh.x, wh.y);
            IPoint[] hullPoints = Andrew_monotone_chain();
            if (!originalReadable)
            {
                var pathToTex = AssetDatabase.GetAssetPath(tex);
                TextureImporter importer = TextureImporter.GetAtPath(pathToTex) as TextureImporter;
                importer.isReadable = false;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            return MakeMeshByDelaunator(hullPoints);
        }

        private void CreatePreview(Mesh mesh)
        {
            if (_previewObject == null)
            {
                _previewObject = new GameObject();
                _previewMeshFilter = _previewObject.AddComponent<MeshFilter>();
                _previewMeshRenderer = _previewObject.AddComponent<MeshRenderer>();
                if (_previewMat == null)
                {
                    Shader shader = Shader.Find(PREVIEW_SHADER_NAME);
                    _previewMat = new Material(shader);
                }
                _previewMeshRenderer.material = _previewMat;
            }
            _previewMeshFilter.sharedMesh = mesh;
            _previewMat.SetTexture(PREVIEW_SHADER_TEX_PROP_NAME, tex);
            Selection.activeGameObject = _previewObject;
        }
        private void Clear()
        {
            DestroyImmediate(_previewObject);
            DestroyImmediate(_previewMat);
            DestroyImmediate(_mesh);
        }

        private void AddPoints(int w, int h)
        {
            singleCellSize = new Vector2Int(w / gridNum.x, h / gridNum.y);
            for (int i = 0; i < w; i++)
            {
                for (int j = 0; j < h; j++)
                {
                    Color pixel = tex.GetPixel(i, j);
                    if (pixel.a > tolerance && (pixel.r + pixel.g + pixel.b) / 3.0f > tolerance)
                    {
                        if (sequenceMode)
                        {
                            _edgepoint.Add(new Vector2Int(fracByGrid(i, singleCellSize.x),
                                fracByGrid(j, singleCellSize.y)));
                        }
                        else
                            _edgepoint.Add(new Vector2Int(i, j));
                    }
                }
            }
        }

        private int fracByGrid(int input, int grid)
        {
            return (input - (input / grid) * grid);
        }

        private double CrossVector(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }
        private static int CompareVector2Int(Vector2Int a, Vector2Int b)
        {
            if (a.x < b.x) return -1;
            if (a.x == b.x)
            {
                if(a.y == b.y)
                    return 0;
                if (a.y < b.y)
                    return -1;
                return 1;
            }
            return 1;
        }

        private Vector2Int[] SortPoint(HashSet<Vector2Int> edgePoints)
        {
            return SortPoint(edgePoints.ToArray());
        }
        private Vector2Int[] SortPoint(Vector2Int[] originalPoints)
        {
            Vector2Int[] sortedPoint = new Vector2Int[originalPoints.Length];
            sortedPoint = originalPoints.ToArray();
            sortedPoint.Sort(CompareVector2Int);
            return sortedPoint;
        }
        
        private IPoint[] Andrew_monotone_chain()
        {
            //https://web.ntnu.edu.tw/~algo/ConvexHull.html
            // 將所有點依照座標大小排序
            Vector2Int[] P = SortPoint(_edgepoint);
            Vector2Int[] L = new Vector2Int[P.Length];
            Vector2Int[] U = new Vector2Int[P.Length];
            int l = 0, u = 0;	// 下凸包的點數、上凸包的點數
            for (int i=0; i<P.Length; ++i)
            {
                while (l >= 2 && CrossVector(L[l-2], L[l-1], P[i]) <= 0) l--;
                while (u >= 2 && CrossVector(U[u-2], U[u-1], P[i]) >= 0) u--;
                L[l++] = P[i];
                U[u++] = P[i];
            }
            //Clean 0,0
            Vector2Int maxSize = new Vector2Int(-1, -1);
            for (int i = 1; i < P.Length; i++)
            {
                if (L[i].x == 0 && L[i].y == 0 && maxSize.x == -1) maxSize.x = i;
                if (U[i].x == 0 && U[i].y == 0 && maxSize.y == -1) maxSize.y = i;
            }

            IPoint[] hullPoints = new IPoint[maxSize.x + maxSize.y];
            for (int i = 0; i < hullPoints.Length; i++)
            {
                Vector2Int src = i < maxSize.x ? L[i] : U[i - maxSize.x];
                hullPoints[i] = new Point(src.x, src.y);
            }
            return hullPoints;
            // outL = new Vector2Int[maxSize.x];
            // outU = new Vector2Int[maxSize.y];
            // Array.ConstrainedCopy(L,0,outL,0,maxSize.x);
            // Array.ConstrainedCopy(U,0,outU,0,maxSize.y);
            // outL = SortPoint(outL);
            // outU = SortPoint(outU);
        }

        // private IPoint[] TransULLine2Points(Vector2Int[] u, Vector2Int[] l)
        // {
        //     IPoint[] points = new IPoint[u.Length + l.Length];
        //     for (int i = 0; i < u.Length; i++)
        //     {
        //         points[i] = new Point(u[i].x, u[i].y);
        //     }
        //
        //     for (int i = u.Length; i < points.Length; i++)
        //     {
        //         points[i] = new Point(l[i-u.Length].x, l[i-u.Length].y);
        //     }
        //     return points;
        // }

        private Mesh MakeMeshByDelaunator(IPoint[] points)
        {
            delaunator = new Delaunator(points);
            Vector2[] editedUV = delaunator.Points.ToVectors2();
            Vector3[] normalizedPos = delaunator.Points.ToVectors3();
            Vector2Int realSize = sequenceMode ? singleCellSize : wh;
            for (int i = 0; i < editedUV.Length; i++)
            {
                editedUV[i].x = editedUV[i].x / realSize.x;
                editedUV[i].y = editedUV[i].y / realSize.y;
                normalizedPos[i].x = editedUV[i].x;
                normalizedPos[i].y = editedUV[i].y;
                normalizedPos[i].z = normalizedPos[i].z;
            }
            Mesh mesh = new Mesh
            {
                vertices = normalizedPos,
                triangles = delaunator.Triangles,
                uv = editedUV
            };
            mesh.RecalculateBounds();
            return mesh;
        }
        
        // private Mesh MakeMesh(Vector2Int[] U, Vector2Int[] L)
        // {
        //     Mesh mesh = new Mesh();
        //     List<Vector3> vertList = new List<Vector3>();
        //     List<Vector2> uvList = new List<Vector2>();
        //     List<int> tri = new List<int>();
        //     List<Color> color = new List<Color>();
        //     int maxCount = Mathf.Max(U.Length, L.Length);
        //     int minCount = Mathf.Min(U.Length, L.Length);
        //     bool UGTL = U.Length > L.Length;
        //     Vector2Int realSize = sequenceMode ? singleCellSize : wh;
        //     for (int i = 0; i < maxCount; i++)
        //     {
        //         if (i < minCount - 1 || minCount == maxCount)
        //         {
        //             Vector2 nU = new Vector2((float)U[i].x / realSize.x,(float)U[i].y / realSize.y);
        //             Vector2 nL = new Vector2((float)L[i].x / realSize.x,(float)L[i].y / realSize.y);
        //             vertList.Add(nU);
        //             vertList.Add(nL);
        //             uvList.Add(nU);
        //             uvList.Add(nL);
        //             color.Add(new Color((float)i/maxCount, 0,0,1));
        //             color.Add(new Color(0, (float)i/maxCount,0,1));
        //             /*
        //              * 0 2 4 ... 2i   2i+2 
        //              * 1 3 5 ... 2i+1 2i+3
        //              */
        //             tri.Add(i * 2);
        //             tri.Add(i * 2 + 2);
        //             tri.Add(i * 2 + 3);
        //             tri.Add(i * 2);
        //             tri.Add(i * 2 + 3);
        //             tri.Add(i * 2 + 1);
        //         }
        //         else if (i == minCount - 1)
        //         {
        //             Vector2 nU = new Vector2((float)U[i].x / realSize.x,(float)U[i].y / realSize.y);
        //             Vector2 nL = new Vector2((float)L[i].x / realSize.x,(float)L[i].y / realSize.y);
        //             vertList.Add(nU);
        //             vertList.Add(nL);
        //             uvList.Add(nU);
        //             uvList.Add(nL);
        //             color.Add(new Color((float)i/maxCount, 0,0,1));
        //             color.Add(new Color(0, (float)i/maxCount,0,1));
        //             if (minCount != maxCount)
        //             {
        //                 /*
        //                  * 0 2 4 ... 2min-2   2min 2min+1 ...
        //                  * 1 3 5 ... 2min-1 
        //                  */
        //                 /*
        //                  * 0 2 4 ... 2min-2   
        //                  * 1 3 5 ... 2min-1 2min 2min+1 ...
        //                  */
        //                 tri.Add(minCount * 2 - 2);
        //                 tri.Add(minCount * 2);
        //                 tri.Add(minCount * 2 - 1);
        //             }
        //         }
        //         else
        //         {
        //             Vector2Int ori = UGTL ? U[i] : L[i];
        //             Vector2 n = new Vector2((float)ori.x / realSize.x,(float)ori.y / realSize.y);
        //             vertList.Add(n);
        //             uvList.Add(n);
        //             color.Add(new Color(0, 0,(float)i/maxCount,1));
        //             if (i != maxCount-1)
        //             {
        //                 if (UGTL)
        //                 {
        //                     /*
        //                      * 0 2 4 ... 2min-2 ... i i+1
        //                      * 1 3 5 ... 2min-1 
        //                      */
        //                     tri.Add(minCount + i);
        //                     tri.Add(minCount + i + 1);
        //                     tri.Add(minCount * 2 - 1);
        //                 }
        //                 else
        //                 {
        //                     /*
        //                      * 0 2 4 ... 2min-2 
        //                      * 1 3 5 ... 2min-1 ... i i+1
        //                      */
        //                     tri.Add(minCount * 2 - 2);
        //                     tri.Add(minCount + i + 1);
        //                     tri.Add(minCount + i);
        //                 }
        //             }
        //         }
        //     }
        //     mesh.vertices = vertList.ToArray();
        //     mesh.triangles = tri.ToArray();
        //     mesh.uv = uvList.ToArray();
        //     if(debugColor)
        //         mesh.colors = color.ToArray();
        //     return mesh;
        // }

    }
} 