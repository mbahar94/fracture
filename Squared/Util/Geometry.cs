﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Squared.Util {
    public delegate void DotProduct<T> (ref T lhs, ref T rhs, out float result);
    public delegate void GetEdgeNormal<T> (ref T first, ref T second, out T result);

    public static class Geometry {
        public static Interval<float> ComputeInterval<T> (T axis, T[] vertices, DotProduct<T> dotProduct) {
            var result = new Interval<float>(0.0f, 0.0f);
            float d = 0.0f;

            result.Min = float.MaxValue;
            result.Max = float.MinValue;

            for (int i = 0; i < vertices.Length; i++) {
                dotProduct(ref vertices[i], ref axis, out d);

                if (d < result.Min)
                    result.Min = d;
                if (d > result.Max)
                    result.Max = d;
            }

            return result;
        }

        public static int GetPolygonAxes<T> (T[] buffer, T[] polygonA, T[] polygonB, GetEdgeNormal<T> getEdgeNormal) {
            int count = 0;
            T axis;

            for (int j = 0; j < 2; j++) {
                var vertexSet = (j == 0) ? polygonA : polygonB;

                bool done = false;
                int i = 0;
                T firstPoint = default(T), previous, current = default(T);

                while (!done) {
                    previous = current;

                    if (i >= vertexSet.Length) {
                        done = true;
                        current = firstPoint;
                    } else {
                        current = vertexSet[i];
                    }

                    if (i == 0) {
                        firstPoint = current;
                        i += 1;
                        continue;
                    }

                    getEdgeNormal(ref previous, ref current, out axis);
                    if (Array.IndexOf<T>(buffer, axis, 0, count) == -1) {
                        buffer[count] = axis;
                        count += 1;
                    }

                    i += 1;
                }
            }

            return count;
        }

        public static bool DoPolygonsIntersect<T> (T[] verticesA, T[] verticesB, DotProduct<T> dotProduct, GetEdgeNormal<T> getEdgeNormal) {
            bool result = true;

            using (var axisBuffer = BufferPool<T>.Allocate(verticesA.Length + verticesB.Length)) {
                int axisCount = GetPolygonAxes<T>(axisBuffer.Data, verticesA, verticesB, getEdgeNormal);

                for (int i = 0; i < axisCount; i++) {
                    var axis = axisBuffer.Data[i];

                    var intervalA = ComputeInterval<T>(axis, verticesA, dotProduct);
                    var intervalB = ComputeInterval<T>(axis, verticesB, dotProduct);

                    bool intersects = intervalA.Intersects(intervalB);

                    if (!intersects)
                        result = false;
                }
            }

            return result;
        }

        public struct IntersectionInfo {
            public bool AreIntersecting;
            public bool WillBeIntersecting;
        }

        public static IntersectionInfo WillPolygonsIntersect<T> (T[] verticesA, T[] verticesB, T relativeTranslationA, DotProduct<T> dotProduct, GetEdgeNormal<T> getEdgeNormal) {
            var result = new IntersectionInfo();
            result.AreIntersecting = true;
            result.WillBeIntersecting = true;

            Interval<float> intervalA, intervalB;
            float translationProjection;

            using (var axisBuffer = BufferPool<T>.Allocate(verticesA.Length + verticesB.Length)) {
                int axisCount = GetPolygonAxes<T>(axisBuffer.Data, verticesA, verticesB, getEdgeNormal);

                for (int i = 0; i < axisCount; i++) {
                    var axis = axisBuffer.Data[i];

                    intervalA = ComputeInterval<T>(axis, verticesA, dotProduct);
                    intervalB = ComputeInterval<T>(axis, verticesB, dotProduct);

                    bool intersects = intervalA.Intersects(intervalB);
                    if (!intersects)
                        result.AreIntersecting = false;

                    dotProduct(ref axis, ref relativeTranslationA, out translationProjection);

                    if (translationProjection > 0) {
                        intervalA.Min -= translationProjection;
                    } else {
                        intervalA.Max -= translationProjection;
                    }

                    intersects = intervalA.Intersects(intervalB);
                    if (!intersects)
                        result.WillBeIntersecting = false;

                    if ((result.WillBeIntersecting == false) && (result.AreIntersecting == false))
                        break;
                }
            }

            return result;
        }
    }
}
