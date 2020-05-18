using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SamDriver.Decal
{
  public class MeshProjection
  {
    const int maxUInt16VertexCount = 65534; // slightly less than UInt16.MaxValue

    // when a triangle is "trimmed" to fit inside the quad region it may be replaced by
    // multiple triangles. The most that can possible come from a triangle is 5.
    // That happens when every triangle edge crosses the decal cube's borders twice.
    const int maxTrianglesFromTrimmedTriangle = 5;

    #region helper structs
    struct RawMesh : IDisposable
    {
      [ReadOnly]
      public readonly NativeArray<int> Indices;
      [ReadOnly]
      public readonly NativeArray<Float3> Positions;
      [ReadOnly]
      public readonly NativeArray<Float3> Normals;

      public int TriangleCount { get => Indices.Length / 3; }

      public RawMesh(
        NativeArray<int> indices_,
        NativeArray<Float3> positions_,
        NativeArray<Float3> normals_)
      {
        this.Indices = indices_;
        this.Positions = positions_;
        this.Normals = normals_;
      }

      public void Dispose()
      {
        Indices.Dispose();
        Positions.Dispose();
        Normals.Dispose(); 
      }
    }

    struct TrimTriangleParallelJob : IJobParallelFor
    {
      readonly RawMesh inputMesh;
      readonly int maxTriangleCount;

      // disable safety so that we can write outside of the single index given to each job
      [Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction]
      NativeArray<Triangle> output;

      public TrimTriangleParallelJob(RawMesh inputMesh_, NativeArray<Triangle> output_, int maxTriangleCount_)
      {
        this.inputMesh = inputMesh_;
        this.output = output_;
        this.maxTriangleCount = maxTriangleCount_;
      }

      public void Execute(int originalTriangleIndex)
      {
        var original = ConstructTriangleFromRawMesh(inputMesh, originalTriangleIndex);

        // skip backfaces
        if (original.GeometryNormal.z >= 0f) return;

        int i = 0;
        foreach (var created in TrimTriangle(original))
        {
          if (i > maxTriangleCount)
          {
            Debug.LogWarning($"Trimming a triangle created more than the expected maximum of {maxTriangleCount}, discarding excess.");
            break;
          }
          output[originalTriangleIndex * maxTriangleCount + i] = created;
          ++i;
        }
      }
    }
    #endregion

    public bool IsReadyToComplete
    {
      get => trimJobHandles != null && trimJobHandles.All(handle => handle.IsCompleted);
    }

    IEnumerable<MeshFilter> meshFilters;
    Transform decalTransform;  
    bool expectToTakeMoreThanFourFrames;

    List<RawMesh> rawMeshes;
    List<NativeArray<Triangle>> allResultingTriangles;
    List<JobHandle> trimJobHandles;

    public MeshProjection(
      IEnumerable<MeshFilter> meshFilters_,
      Transform decalTransform_,
      bool expectToTakeMoreThanFourFrames_ = false)
    {
      this.meshFilters = meshFilters_;
      this.decalTransform = decalTransform_;
      this.expectToTakeMoreThanFourFrames = expectToTakeMoreThanFourFrames_;
      Begin();
    }

    void Begin()
    {
      rawMeshes = new List<RawMesh>();
      allResultingTriangles = new List<NativeArray<Triangle>>();
      trimJobHandles = new List<JobHandle>();

      // begin jobs
      ScheduleTrimJobs(ref rawMeshes, ref allResultingTriangles, ref trimJobHandles, decalTransform, meshFilters,
        expectToTakeMoreThanFourFrames, false);
    }

    public void Abort()
    {
      // even though we don't need their work, we have to call Complete to give
      // the main thread ownership of NativeArrays ready for their disposal
      foreach (var jobHandle in trimJobHandles) jobHandle.Complete();
      foreach (var resultingTriangles in allResultingTriangles) resultingTriangles.Dispose();
      rawMeshes.ForEach(rawMesh => rawMesh.Dispose());
    }

    public UnityEngine.Mesh Complete()
    {
      // require jobs to complete, potentially pausing main thread until they do
      var resultantTriangles = ReceiveTrimResults(allResultingTriangles, trimJobHandles).ToList();

      // clean up
      foreach (var resultingTriangles in allResultingTriangles) resultingTriangles.Dispose();
      rawMeshes.ForEach(rawMesh => rawMesh.Dispose());

      // use generated triangles to make a Unity mesh (this is still a fairly heavy process in terms of performance)
      return BuildMesh(resultantTriangles, new Float3(decalTransform.localScale));
    }

    static void ScheduleTrimJobs(
      ref List<RawMesh> rawMeshes,
      ref List<NativeArray<Triangle>> allResultingTriangles,
      ref List<JobHandle> trimJobHandles,
      Transform decalTransform,
      IEnumerable<MeshFilter> meshFilters,
      bool shouldUsePersistentAllocation = false,
      bool immediatelyStart = false
    )
    {
      // directly accessing the Mesh data is only possible on primary thread, so
      // get all that accessing done here and packed into NativeArrays
      foreach (var meshFilter in meshFilters)
      {
        // Allocator.TempJob expects to last for <= 4 frames
        // Allocator.Persistent can last indefinitely
        var allocator = shouldUsePersistentAllocation ? Allocator.Persistent : Allocator.TempJob;

        // mesh as read from meshFilter is defined in that object's local space
        var mesh = meshFilter.sharedMesh;
        if (!mesh.isReadable)
        {
          // can only project against meshes set up to be readable
          continue;
        }

        NativeArray<int> indices = new NativeArray<int>(mesh.triangles, allocator);
        NativeArray<Float3> positions = new NativeArray<Float3>(mesh.vertexCount, allocator);
        NativeArray<Float3> normals = new NativeArray<Float3>(mesh.vertexCount, allocator);
        for (int i = 0; i < mesh.vertexCount; ++i)
        {
          positions[i] = new Float3(mesh.vertices[i]);
          normals[i] = new Float3(mesh.normals[i]);
        }
        TransformPointsFromLocalAToLocalB(ref positions, meshFilter.transform, decalTransform);
        TransformDirectionsFromLocalAToLocalB(ref normals, meshFilter.transform, decalTransform);

        // rawMesh is defined in decal's local space
        var rawMesh = new RawMesh(indices, positions, normals);

        // sparse array of trimmed triangles which will be filled by the parallel jobs
        var resultingTriangles = new NativeArray<Triangle>(rawMesh.TriangleCount * maxTrianglesFromTrimmedTriangle, allocator);
        var trimJob = new TrimTriangleParallelJob(rawMesh, resultingTriangles, maxTrianglesFromTrimmedTriangle);

        int batchCount = 1; // may want to tweak batchCount for performance
        var trimJobHandle = trimJob.Schedule(rawMesh.TriangleCount, batchCount);
        // one [collective noun] of parallel jobs is created for each MeshFilter

        rawMeshes.Add(rawMesh);
        allResultingTriangles.Add(resultingTriangles);
        trimJobHandles.Add(trimJobHandle);
      }

      if (immediatelyStart)
      {
        JobHandle.ScheduleBatchedJobs();
      }
    }

    /// <summary>
    /// Enforces the completion of all jobs handled by trimJobHandles.
    /// </summary>
    static IEnumerable<Triangle> ReceiveTrimResults(
      List<NativeArray<Triangle>> allResultingTriangles,
      List<JobHandle> trimJobHandles
    )
    {
      for (int jobIndex = 0; jobIndex < trimJobHandles.Count; ++jobIndex)
      {
        trimJobHandles[jobIndex].Complete();
        // we now know that the set of parallel jobs is completed, and have regained ownership of the NativeArray<Triangle>
        // Reminder: each job handle represents one mesh being handled by a set of parallel jobs

        var trianglesFromParallelJobs = allResultingTriangles[jobIndex];
        foreach (var triangle in trianglesFromParallelJobs)
        {
          if (triangle.IsPresent)
          {
            yield return triangle;
          }
        }
      }
    }

    /// <summary>
    /// Immediately create a projected decal mesh, using the given meshFilters.
    /// </summary>
    public static UnityEngine.Mesh GenerateProjectedDecalMesh(
      IEnumerable<MeshFilter> meshFilters,
      Transform decalTransform)
    {
      var rawMeshes = new List<RawMesh>();
      var allResultingTriangles = new List<NativeArray<Triangle>>();
      var trimJobHandles = new List<JobHandle>();

      // begin jobs
      ScheduleTrimJobs(ref rawMeshes, ref allResultingTriangles, ref trimJobHandles, decalTransform, meshFilters, false, true);

      // immediately require jobs to complete, pausing main thread until they do
      var resultantTriangles = ReceiveTrimResults(allResultingTriangles, trimJobHandles).ToList();

      // clean up
      foreach (var resultingTriangles in allResultingTriangles) resultingTriangles.Dispose();
      rawMeshes.ForEach(rawMesh => rawMesh.Dispose());

      // use generated triangles to make a Unity mesh
      return BuildMesh(resultantTriangles, new Float3(decalTransform.localScale));
    }

    static void TransformPointsFromLocalAToLocalB(
      ref NativeArray<Float3> points,
      Transform transformA,
      Transform transformB)
    {
      for (int i = 0; i < points.Length; ++i)
      {
        Vector3 localA = points[i].AsVector3;
        Vector3 world = transformA.TransformPoint(points[i].AsVector3);
        Vector3 localB = transformB.InverseTransformPoint(world);
        points[i] = new Float3(localB);
      }
    }

    static void TransformDirectionsFromLocalAToLocalB(
      ref NativeArray<Float3> directions,
      Transform transformA,
      Transform transformB)
    {
      for (int i = 0; i < directions.Length; ++i)
      {
        Quaternion aToWorld = transformA.rotation;
        Quaternion worldToB = Quaternion.Inverse(transformB.rotation);
        Quaternion aToB = worldToB * aToWorld;

        Vector3 localB = aToB * directions[i].AsVector3;
        directions[i] = new Float3(localB);
      }
    }

    static Triangle ConstructTriangleFromRawMesh(RawMesh rawMesh, int triangleIndex)
    {
      int indexA = rawMesh.Indices[triangleIndex * 3 + 0];
      int indexB = rawMesh.Indices[triangleIndex * 3 + 1];
      int indexC = rawMesh.Indices[triangleIndex * 3 + 2];

      return new Triangle(
        new Vertex(rawMesh.Positions[indexA], rawMesh.Normals[indexA]),
        new Vertex(rawMesh.Positions[indexB], rawMesh.Normals[indexB]),
        new Vertex(rawMesh.Positions[indexC], rawMesh.Normals[indexC])
      );
    }

    /// <summary>
    /// Given triangle should be defined in decal's local space.
    /// </summary>
    static IEnumerable<Vertex> VerticesOfTrimmedTriangle(Triangle originalTriangle)
    {
      bool isOriginalWhollyWithin = true;
      foreach (Vertex originalVertex in originalTriangle)
      {
        if (
          originalVertex.Position.x >= -0.5f &&
          originalVertex.Position.x <=  0.5f &&
          originalVertex.Position.y >= -0.5f &&
          originalVertex.Position.y <=  0.5f &&
          originalVertex.Position.z >= -0.5f &&
          originalVertex.Position.z <=  0.5f)
        {
          // vertices within decal bounds remain as they are
          yield return originalVertex;
        }
        else
        {
          isOriginalWhollyWithin = false;
        }
      }

      //TODO: could also exclude triangles which are definitely outside the region.
      // All of a triangle's vertices being outside the cube does not mean the triangle is,
      // the edges may pass through the cube and would still need to be considered.
      // All the vertices being on the same side of one of the cube borders would correctly
      // identify it as being fully outside.

      if (isOriginalWhollyWithin)
      {
        // all the vertices are within decal's cube, and have already been yielded
        yield break;
      }

      // where an edge of decal's unit cube intersects this triangle, add a vertex
      foreach (var squareCorner in Square.SquareCorners())
      {
        if (originalTriangle.IsAxialXLineWithin(squareCorner.x, squareCorner.y))
        {
          float x = originalTriangle.getXAtYZ(squareCorner.x, squareCorner.y);
          if (x > -0.5f && x < 0.5f)
          {
            Float3 position = new Float3(x, squareCorner.x, squareCorner.y);
            Float3 normal = originalTriangle.InterpolateNormal(position);
            yield return new Vertex(position, normal);
          }
        }

        if (originalTriangle.IsAxialYLineWithin(squareCorner.x, squareCorner.y))
        {
          float y = originalTriangle.getYAtXZ(squareCorner.x, squareCorner.y);
          if (y > -0.5f && y < 0.5f)
          {
            Float3 position = new Float3(squareCorner.x, y, squareCorner.y);
            Float3 normal = originalTriangle.InterpolateNormal(position);
            yield return new Vertex(position, normal);
          }
        }

        if (originalTriangle.IsAxialZLineWithin(squareCorner.x, squareCorner.y))
        {
          float z = originalTriangle.GetZAtXY(squareCorner.x, squareCorner.y);
          if (z > -0.5f && z < 0.5f)
          {
            Float3 position = new Float3(squareCorner.x, squareCorner.y, z);
            Float3 normal = originalTriangle.InterpolateNormal(position);
            yield return new Vertex(position, normal);
          }
        }
      }

      // when an edge crosses the quad borders, generate a vertex at each crossing point
      foreach (Edge originalEdge in originalTriangle.EnumerateEdges())
      {
        foreach (Vertex crossingPoint in QuadCrossings(originalEdge))
        {
          yield return crossingPoint;
        }
      }
    }

    /// <summary>
    /// Find any crossing points for an edge against the centered unit cube.
    /// A single edge may produce 0, 1 or 2 crossing points.
    /// This does NOT generate a vertex for an edge that passes exactly
    /// through a corner point.
    /// </summary>
    static IEnumerable<Vertex> QuadCrossings(Edge edge)
    {
      // foreach (Dimension dimension in Enum.GetValues(typeof(Dimension)))
      foreach (var dimension in DimensionHelper.Enumerate())
      {
        foreach (var vertex in BorderCrossing(edge, dimension, false)) yield return vertex;
        foreach (var vertex in BorderCrossing(edge, dimension, true)) yield return vertex;
      }
    }

    static IEnumerable<Vertex> BorderCrossing(Edge edge, Dimension borderDimension, bool isBorderPositive)
    {
      float t = edge.InverseLerpDimension(isBorderPositive ? 0.5f : -0.5f, borderDimension);
      if (!IsIn01RangeExclusive(t))
      {
        yield break;
      }
      
      // edge does cross this border, but only interested in crossings within region
      bool isCrossingInRegion = true;
      foreach (Dimension otherDimension in Enum.GetValues(typeof(Dimension)))
      {
        // only interested in other dimensions
        if (otherDimension == borderDimension) continue; 

        // find where the edge crosses border, and check if that's within region
        float atCrossing = edge.LerpDimension(t, otherDimension);
        if (!IsInCenteredUnitRangeExclusive(atCrossing))
        {
          isCrossingInRegion = false;
          break;
        }
      }

      if (isCrossingInRegion)
      {
        yield return edge.GetVertexBetween(t);
      }
    }

    /// <summary>
    /// Given a mesh triangle produce a set of triangles that represent
    /// the same shape but trimmed to where it intersects with the decal region.
    /// Will produce zero triangles if originalTriangle is wholly outside region.
    /// </summary>
    static IEnumerable<Triangle> TrimTriangle(Triangle originalTriangle)
    {
      // generate a set of vertices, which are then reconstructed into triangle(s)
      // will be iterating through and sorting this, so make a list to work with
      List<Vertex> verticesList = VerticesOfTrimmedTriangle(originalTriangle).ToList();

      if (verticesList.Count < 3)
      {
        if (verticesList.Count > 0)
        {
          Debug.LogWarning($"Received {verticesList.Count} vertices which is unexpected. Shouldn't be a case where there's fewer than 3 but non-zero.");
        }

        // not enough vertices to make any triangles
        yield break;
      }

      // sort the vertices so they're in counter-clockwise order around a mid point
      float midX = verticesList.Average(vertex => vertex.Position.x);
      float midY = verticesList.Average(vertex => vertex.Position.y);
      verticesList.Sort((a, b) => {
        float angleA = AngleToProjectedVertex(midX, midY, a);
        float angleB = AngleToProjectedVertex(midX, midY, b);
        return -angleA.CompareTo(angleB);
      });

      // create fan of triangles with verticesList[0] as common
      int triangleCountToGenerate = (verticesList.Count - 2);
      for (int triangleIndex = 0; triangleIndex < triangleCountToGenerate; ++triangleIndex)
      {
        yield return new Triangle(
          verticesList[0],
          verticesList[triangleIndex + 1],
          verticesList[triangleIndex + 2]
        );
      }
    }

    /// <summary>
    /// Angle in radians of line from midpoint (midX, midY) to target,
    /// in range -π to +π
    /// 0 means target is along the +ve x axis from midpoint.
    /// +0.5π means target is along the +ve y axis from midpoint.
    /// </summary>
    static float AngleToProjectedVertex(float midX, float midY, Vertex target)
    {
      float dx = target.Position.x - midX;
      float dy = target.Position.y - midY;
      return Mathf.Atan2(dy, dx);
    }

    static bool IsIn01RangeExclusive(float value)
    {
      return (value > 0f) && (value < 1f);
    }
    static bool IsInCenteredUnitRangeExclusive(float value)
    {
      return (value > -0.5f) && (value < 0.5f);
    }

    // from: http://answers.unity.com/comments/190515/view.html
    // which in turn is based on: http://foundationsofgameenginedev.com/FGED2-sample.pdf
    static void CalculateMeshTangents(Mesh mesh)
    {
      int[] indices = mesh.triangles;
      Vector3[] positions = mesh.vertices;
      Vector2[] uvs = mesh.uv;
      Vector3[] normals = mesh.normals;

      int indexCount = indices.Length;
      int vertexCount = positions.Length;

      Vector3[] tan1 = new Vector3[vertexCount];
      Vector3[] tan2 = new Vector3[vertexCount];

      Vector4[] tangents = new Vector4[vertexCount];

      for (int a = 0; a < indexCount; a += 3)
      {
        int i1 = indices[a + 0];
        int i2 = indices[a + 1];
        int i3 = indices[a + 2];

        Vector3 v1 = positions[i1];
        Vector3 v2 = positions[i2];
        Vector3 v3 = positions[i3];

        Vector2 w1 = uvs[i1];
        Vector2 w2 = uvs[i2];
        Vector2 w3 = uvs[i3];

        float x1 = v2.x - v1.x;
        float x2 = v3.x - v1.x;
        float y1 = v2.y - v1.y;
        float y2 = v3.y - v1.y;
        float z1 = v2.z - v1.z;
        float z2 = v3.z - v1.z;

        float s1 = w2.x - w1.x;
        float s2 = w3.x - w1.x;
        float t1 = w2.y - w1.y;
        float t2 = w3.y - w1.y;

        // avoid div0
        float denom = (s1 * t2 - s2 * t1);
        if (denom == 0f) denom = 0.0001f;

        float r = 1.0f / denom;

        Vector3 sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
        Vector3 tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

        tan1[i1] += sdir;
        tan1[i2] += sdir;
        tan1[i3] += sdir;

        tan2[i1] += tdir;
        tan2[i2] += tdir;
        tan2[i3] += tdir;
      }

      for (long a = 0; a < vertexCount; ++a)
      {
        Vector3 n = normals[a];
        Vector3 t = tan1[a];

        Vector3.OrthoNormalize(ref n, ref t);
        tangents[a].x = t.x;
        tangents[a].y = t.y;
        tangents[a].z = t.z;

        tangents[a].w = (Vector3.Dot(Vector3.Cross(n, t), tan2[a]) < 0.0f) ? -1.0f : 1.0f;
      }

      mesh.tangents = tangents;
    }

    static Mesh BuildMesh(IEnumerable<Triangle> triangles, Float3 localScale)
    {
      List<Vector3> positions = new List<Vector3>();
      List<Vector2> uvs = new List<Vector2>();
      List<Vector3> normals = new List<Vector3>();
      List<int> indices = new List<int>();

      int triangleIndex = 0;
      bool tooManyTrianglesForUInt16 = false;
      foreach (Triangle triangle in triangles)
      {
        int vertexIndexWithinTriangle = 0;
        // Vector3 normal = triangle.GeometryNormal;
        foreach (Vertex vertex in triangle)
        {
          positions.Add(vertex.Position.AsVector3);
          normals.Add((vertex.Normal * localScale).AsVector3);
          uvs.Add(new Vector2(
            vertex.Position.x + 0.5f,
            vertex.Position.y + 0.5f
          ));
          indices.Add(triangleIndex * 3 + vertexIndexWithinTriangle);
          ++vertexIndexWithinTriangle;
        }
        ++triangleIndex;

        if (triangleIndex > maxUInt16VertexCount / 3)
        {
          tooManyTrianglesForUInt16 = true;
          if (triangleIndex > int.MaxValue / 3)
          {
            Debug.LogWarning($"MeshProjection attempting to create extremely large mesh with over {triangleIndex.ToString("N0")} triangles, which cannot be represented in a single mesh. The excess triangles will be discarded."); 
            break;
          }
        }
      }

      var mesh = new Mesh();

      if (tooManyTrianglesForUInt16)
      {
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        // when using UInt32 as index format the max index should be 4.2 billion,
        // but Unity stores mesh.triangles as an int[] so actual limit is 2.1 billion
        // (that is still a very large number of vertices)
      }
      mesh.vertices = positions.ToArray();
      mesh.normals = normals.ToArray();
      mesh.uv = uvs.ToArray();
      mesh.triangles = indices.ToArray();

      //TODO: could roll this into the overall mesh generation rather than separating it out 
      CalculateMeshTangents(mesh);
      return mesh;
    }

    /*
    /// <summary>
    /// Not used, and not tested.
    /// Filters out colinear vertices. Which is a useful step in mesh simplification.
    /// The other steps aren't implemented yet. Mesh simplification would be neat,
    /// but the rendering performance gain in most situations would be tiny.
    /// </summary>
    static IEnumerable<Vertex> RemoveColinearVertices(IEnumerable<Vertex> originalVertices)
    {
      List<Vertex> cache = originalVertices.ToList();
      if (cache.Count == 0) yield break;

      // to save headaches about removal during enumeration, build a set of vertices to remove
      HashSet<Vertex> verticesToRemove = new HashSet<Vertex>();

      foreach (Vertex start in cache)
      {
        if (verticesToRemove.Contains(start)) continue;

        // build collection of unique directions from other vertices to start,
        // while building it also find any colinear vertices we can remove
        HashSet<DirectionToVertex> directionsFromStart = new HashSet<DirectionToVertex>();

        foreach (Vertex a in cache)
        {
          if (verticesToRemove.Contains(a)) continue;
          if (a.Equals(start)) continue;

          DirectionToVertex startToA = new DirectionToVertex(start, a);
          bool shouldAddDirection = true;

          foreach (var startToB in directionsFromStart.Where(direction => direction.RoughlyEqualDirection(startToA)))
          {
            // the following vertices are colinear: start, A, startToB.target,
            // because direction to start is (roughly) the same for A and startToB.target, we know
            // the middle of the 3 cannot be start. The middle is whichever of A and B is closest to start.
            if (startToA.distancetoTarget <= startToB.distancetoTarget)
            {
              // A is the middle vertex
              verticesToRemove.Add(a);

              // no need to add this direction to the collection, as other colinears will be 
              // detected thanks to the startToB direction which detected this one
              shouldAddDirection = false;

              // no need to check any other parallel directions
              // wouldn't expect there to be other parallels anyway (might want to check that!)
              break;
            }
            else
            {
              // B is the middle vertex
              verticesToRemove.Add(startToB.target);

              // remove record of B's direction, replacing it with A's direction
              directionsFromStart.Remove(startToB);
              shouldAddDirection = true;
            }
          }

          if (shouldAddDirection)
          {
            directionsFromStart.Add(startToA);
          }
        }
      }

      foreach (Vertex item in cache)
      {
        if (!verticesToRemove.Contains(item))
        {
          yield return item;
        }
      }
    }
    */
  }
}
