using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Baseline;
using Marten.Util;
using Npgsql;
using NpgsqlTypes;

namespace Marten.Linq
{
    public class ContainmentWhereFragment : IWhereFragment
    {
        private readonly IDictionary<string, object> _dictionary;
        private readonly ISerializer _serializer;


        public ContainmentWhereFragment(ISerializer serializer, IDictionary<string, object> dictionary)
        {
            _serializer = serializer;
            _dictionary = dictionary;
        }

        public ContainmentWhereFragment(ISerializer serializer, BinaryExpression binary)
            : this(serializer, new Dictionary<string, object>())
        {
            CreateDictionaryForSearch(binary, _dictionary);
        }

        public void Apply(CommandBuilder builder)
        {
            var json = _serializer.ToCleanJson(_dictionary);
            var param = builder.AddParameter(json);
            param.NpgsqlDbType = NpgsqlDbType.Jsonb;

            builder.Append("d.data @> :");
            builder.Append(param.ParameterName);
        }

        public bool Contains(string sqlText)
        {
            return false;
        }

        public static void CreateDictionaryForSearch(BinaryExpression binary, IDictionary<string, object> dict)
        {
            var expressionValue = binary.Right.Value();
            var memberExpression = binary.Left;

            CreateDictionaryForSearch(dict, memberExpression, expressionValue);
        }

        public static void CreateDictionaryForSearch(IDictionary<string, object> dict, Expression memberExpression,
            object expressionValue)
        {
            var visitor = new FindMembers();
            visitor.Visit(memberExpression);

            var members = visitor.Members;

            if (members.Count > 1)
            {
                var temp = new Dictionary<string, object>();
                var member = members.Last();
                var value = expressionValue;
                temp.Add(member.Name, value);

                members.Reverse().Skip(1).Each(m => { temp = new Dictionary<string, object> {{m.Name, temp}}; });

                var topMemberName = members.First().Name;
                dict.Add(topMemberName, temp[topMemberName]);
            }
            else
            {
                var member = members.Single();
                var value = expressionValue;
                dict.Add(member.Name, value);
            }
        }

        public static IWhereFragment SimpleArrayContains(MemberInfo[] members, ISerializer serializer, Expression @from, object value)
        {
            if (value != null)
            {
                var array = Array.CreateInstance(value.GetType(), 1);
                array.SetValue(value, 0);

                var dict = new Dictionary<string, object> {{members.Last().Name, array}};

                members.Reverse().Skip(1).Each(m => { dict = new Dictionary<string, object> {{m.Name, dict}}; });

                return new ContainmentWhereFragment(serializer, dict);
            }


            throw new NotSupportedException();
        }
    }
}