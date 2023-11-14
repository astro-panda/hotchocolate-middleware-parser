using HotChocolate.Data.Filters;
using HotChocolate.Data.Sorting;
using HotChocolate.Resolvers;
using HotChocolate.Types.Pagination;
using Humanizer;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace HotChocolateMiddlewareParser;

/// <summary>
/// A parser for the HotChocolate middleware. For when your data source is not sql and things need to be handled manually
/// </summary>
public class HotChocolateMiddlewareParser<T>
{
    /// <param name="dataSource">The source for your data</param>
    /// <param name="resolver">The resolver context from the middleware</param>
    /// <param name="filter">The filter context from the middleware</param>
    /// <param name="sorting">The sorting context from the middleware</param>
    /// <param name="defaultPagingValue">The default value for paging when neither a first nor a last value were provided</param>
    /// <param name="propertyMapper">An optional property mapper for when the type of your data source does not match the Hot Chocolate type</param>
    public HotChocolateMiddlewareParser(IQueryable<T> dataSource,
                                        IResolverContext resolver,
                                        IFilterContext filter,
                                        ISortingContext sorting,
                                        int defaultPagingValue = 10,
                                        Dictionary<string, string> propertyMapper = null)
    {
        Data = dataSource ?? Enumerable.Empty<T>().AsQueryable();
        _resolver = resolver;
        _filter = filter;
        _sorting = sorting;
        _defaultPagingValue = defaultPagingValue;
        _propertyMapper = new Dictionary<string, string>();
        if (propertyMapper is not null && propertyMapper.Count > 0)
        {
            // ToLowering the keys so that a casing problem does not arise since HotChocolate uses camelCase and C# uses PascalCase
            foreach (var property in propertyMapper)
                _propertyMapper.Add(property.Key.ToLower(), property.Value);

            _usingPropertyMapper = true;
        }

        if (_resolver is not null)
        {
            _pagingArgs = new CursorPagingArguments(
                    first: _resolver?.ArgumentValue<int?>("first"),
                    after: _resolver?.ArgumentValue<string>("after"),
                    last: _resolver?.ArgumentValue<int?>("last"),
                    before: _resolver?.ArgumentValue<string>("before")
                );
            // Defaulting to the first if it is provided or if neither are
            _usingFirst = _pagingArgs.First is not null || _pagingArgs.Last is null;

            try
            {
                if (Data.TryGetNonEnumeratedCount(out var count))
                    _totalCount = count;
                else
                    _totalCount = Data.AsQueryable().Count();
            }
            catch (NotSupportedException ex)
            {
                _totalCount = 0;
            }

            if (TryFromBase64(_pagingArgs.After, out int after))
                after++;

            _afterValue = after;
            _firstValue = _pagingArgs.First ?? _defaultPagingValue;

            bool hasNextPage = _totalCount > _firstValue + _afterValue;
            bool hasPreviousPage = _afterValue > 0;
            _pageInfo = new(hasNextPage, hasPreviousPage, ToBase64(_afterValue), ToBase64(_afterValue + _firstValue));
        }
    }

    public IQueryable<T> Data { get; set; }

    private int _defaultPagingValue { get; set; }
    private Dictionary<string, string> _propertyMapper { get; set; }
    private bool _usingPropertyMapper { get; set; } = false;
    private IResolverContext _resolver { get; set; }
    private IFilterContext _filter { get; set; }
    private ISortingContext _sorting { get; set; }
    private ParameterExpression _parameter = Expression.Parameter(typeof(T));
    private bool _usingFirst { get; set; } = true;
    private CursorPagingArguments _pagingArgs { get; set; }
    private ConnectionPageInfo _pageInfo { get; set; }
    private int _totalCount { get; set; } = 0;
    private int _afterValue { get; set; } = 0;
    private int _firstValue { get; set; } = 0;

    /// <summary>
    /// Filters, sorts, and pages the data source. It marks the filtering and sorting middleware as handled
    /// </summary>
    /// <returns>The filtered, sorted, and paged data source (i.e. <see cref="Data"/>)</returns>
    public IQueryable<T> Parse()
    {
        HandleFilter();
        HandleSorting();
        HandlePaging();
        // If we flip the order in order to use the 'last' value from paging (see comment in HandleSorting) we need to reorder it
        // to the requested order. Otherwise it would be returned in the opposite order
        if (!_usingFirst)
        {
            // We have to enumerate it and then reorder since a lot of IQueryable data sources cannot order after handling paging
            Data = Data.ToList().AsQueryable();
            HandleSorting(true);
        }
        return Data;
    }

    /// <summary>
    /// Handles the filtering from the HotChocolate middleware
    /// </summary>
    public void HandleFilter()
    {
        Expression fullExpression = null;
        if (_filter is not null)
        {
            var filterDict = _filter.ToDictionary() ?? new Dictionary<string, object?>();
            foreach (var filter in filterDict)
            {
                Expression filterExpression = null;
                switch (filter.Key)
                {
                    case "or":
                        foreach (var expression in GetFilterExpressions((object[])filter.Value))
                        {
                            if (filterExpression is null)
                                filterExpression = expression;
                            else
                                filterExpression = Expression.Or(filterExpression, expression);
                        }
                        break;
                    case "and":
                        foreach (var expression in GetFilterExpressions((object[])filter.Value))
                        {
                            if (filterExpression is null)
                                filterExpression = expression;
                            else
                                filterExpression = Expression.And(filterExpression, expression);
                        }
                        break;
                    default:
                        foreach (var expression in _getFilterExpressions(filter))
                        {
                            if (filterExpression is null)
                                filterExpression = expression;
                            else
                                filterExpression = Expression.And(filterExpression, expression);
                        }
                        break;
                }
                if (filterExpression is not null)
                {
                    if (fullExpression is null)
                        fullExpression = filterExpression;
                    else
                        fullExpression = Expression.And(fullExpression, filterExpression);
                }
            }
            if (fullExpression is not null)
            {
                var whereLambda = Expression.Lambda<Func<T, bool>>(fullExpression, _parameter);
                Data = Data.Where(whereLambda);
            }
            _filter.Handled(true);
        }
    }

    /// <summary>
    /// Handles the sorting from the HotChocolate middleware
    /// </summary>
    public void HandleSorting(bool reorder = false)
    {
        IOrderedQueryable<T> orderedData = null;
        if (_sorting is not null)
        {
            var sortingList = _sorting.ToList();
            if (sortingList.Count > 0)
            {
                foreach (var sorting in sortingList[0])
                {
                    MemberExpression property = _getProperty(sorting.Key);

                    var keySelector = Expression.Lambda<Func<T, object>>(property, _parameter);
                    bool orderAsc = (string)sorting.Value == "ASC";
                    // When using the last value from paging we need to reorder it after everything is done
                    // and this will ensure that the proper ordering takes place below
                    if (reorder)
                        orderAsc = !orderAsc;

                    if (orderedData is null)
                    {
                        // This is an XNOR gate. I needed ((!orderAsc && !_usingFirst) || (orderAsc && _usingFirst))
                        // but realized that it was equivalent to an XNOR and I found that in C# you can do (orderAsc == _usingFirst)

                        // This is flipping the ordering if paging will be using the 'last' value. To grab the last 5 for a specific ordering we
                        // need to flip the ordering and then handle paging (see comment at end of function)
                        if (orderAsc == _usingFirst)
                            orderedData = Data.OrderBy(keySelector);
                        else
                            orderedData = Data.OrderByDescending(keySelector);
                    }
                    else
                    {
                        if (orderAsc == _usingFirst)
                            orderedData = orderedData.ThenBy(keySelector);
                        else
                            orderedData = orderedData.ThenByDescending(keySelector);
                    }
                }
                if (orderedData is not null)
                    Data = orderedData;
            }
            // This marks sorting as having been handled if we are using the 'first' value from paging so that the middleware doesn't do it again. 
            // But if we flip the order in order to use the 'last' value from paging (see above comment) we need to let the middleware reorder it
            // to the requested order. Otherwise it would be returned in the opposite order
            if (!_usingFirst)
                _sorting.Handled(true);
        }
    }

    /// <summary>
    /// Handles the paging from the HotChocolate middleware <br/><br/>
    /// Note: The before paging argument is not currently supported
    /// </summary>
    public void HandlePaging()
    {
        if (_resolver is not null)
        {
            // If _usingFirst is true, the ordering should match the requested, if it is false it should be flipped so that we can grab
            // the last x entries (see comments in the HandleSorting function)
            if (_usingFirst)
                Data = Data.Skip(_afterValue).Take(_pagingArgs.First ?? _defaultPagingValue);
            else
                Data = Data.Skip(_afterValue).Take(_pagingArgs.Last ?? _defaultPagingValue);
        }
    }

    /// <summary>
    /// Converts a value from a base 64 string to an int
    /// </summary>
    /// <param name="stringValue">The base 64 string to convert to an int</param>
    /// <param name="intValue">The int resulting from the conversion</param>
    /// <returns><c>true</c> if the conversion was successful<br/><c>false</c> if the conversion was not successful</returns>
    public bool TryFromBase64(string? stringValue, out int intValue)
    {
        var result = int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(stringValue ?? "")), out int intermediateInt);
        intValue = intermediateInt;
        return result;
    }

    /// <summary>
    /// Converts a value from an int to its corresponding value in base 64 as a string
    /// </summary>
    /// <param name="intValue">The int to convert to base 64</param>
    /// <returns>The base 64 string equivalent to the int provided</returns>
    public string ToBase64(int intValue)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(intValue.ToString()));
    }

    /// <summary>
    /// Parses the <see cref="IFilterContext"/> from HotChocolate
    /// </summary>
    /// <param name="filters"></param>
    /// <returns>The parsed filter expressions</returns>
    /// <exception cref="KeyNotFoundException">Thrown when you provide a property mapper in the constructor but Hot Chocolate is trying to filter on a property that is not in the property mapper</exception>
    public IEnumerable<Expression> GetFilterExpressions(object[] filters)
    {
        var expressions = new List<Expression>();
        foreach (var α in filters)
        {
            foreach (DictionaryEntry ß in (IDictionary)α)
            {
                expressions.AddRange(_getFilterExpressions(new KeyValuePair<string, object>((string)ß.Key, ß.Value)));
            }
        }
        return expressions;
    }

    /// <summary>
    /// Pulled this functionality out of <see cref="GetFilterExpressions(object[])"/> so that it can be used in <see cref="HandleFilter"/>
    /// </summary>
    /// <param name="entry">The <see cref="KeyValuePair"/> from the <see cref="IFilterContext"/></param>
    /// <returns>The parsed filter expressions</returns>
    private IEnumerable<Expression> _getFilterExpressions(KeyValuePair<string, object> entry)
    {
        MemberExpression property = _getProperty(entry.Key);
        var propType = ((PropertyInfo)property.Member).PropertyType;

        foreach (DictionaryEntry filterValue in (IDictionary)entry.Value)
        {
            var value = Expression.Constant(filterValue.Value);
            if (value.Type != propType)
            {
                value = _convertConstantType(value, propType);
            }
            Expression expression = _getExpression((string)filterValue.Key, property, value);
            if (expression is not null)
            {
                yield return expression;
            }
        }
    }

    /// <summary>
    /// Creates an expression from the property, operation, and the constant value
    /// </summary>
    /// <param name="operation">The operation used in the expression</param>
    /// <param name="property">The <see cref="MemberExpression"/> that represents the property for the expression</param>
    /// <param name="value">The <see cref="ConstantExpression"/> that the property is compared against</param>
    /// <returns>The <see cref="Expression"/> created from the property, operation, and the constant value. Will return null if the operation is not currently supported</returns>
    private Expression _getExpression(string operation, MemberExpression property, ConstantExpression value)
    {
        switch (operation)
        {
            case "eq":
                return Expression.Equal(property, value);
            case "neq":
                return Expression.NotEqual(property, value);
            case "contains":
                MethodInfo containsMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
                return Expression.Call(property, containsMethod, value);
            case "endsWith":
                MethodInfo startsWithMethod = typeof(string).GetMethod("StartsWith", new[] { typeof(string) });
                return Expression.Call(property, startsWithMethod, value);
            case "startsWith":
                MethodInfo endsWithMethod = typeof(string).GetMethod("Contains", new[] { typeof(string) });
                return Expression.Call(property, endsWithMethod, value);
            case "lt": // Less Than
            case "ngte": // Not Greater Than Or Equal
                return Expression.LessThan(property, value);
            case "lte": // Less Than Or Equal
            case "ngt": // Not Greater Than
                return Expression.LessThanOrEqual(property, value);
            case "gt": // Greater Than
            case "nlte": // Not Less Than Or Equal
                return Expression.GreaterThan(property, value);
            case "gte": // Greater Than Or Equal
            case "nlt":// Not Less Than
                return Expression.GreaterThanOrEqual(property, value);
            default:
                return null;
        }
    }

    /// <summary>
    /// Gets the <see cref="MemberExpression"/> from the property name. Uses the property mapper if it is in use
    /// </summary>
    /// <param name="propName">The <see cref="string"/> value of the property name</param>
    /// <returns>The <see cref="MemberExpression"/> representing the property on the class</returns>
    /// <exception cref="KeyNotFoundException">Throws this when a property mapper is in use but HotChocolate tries to use a property not in the mapper</exception>
    private MemberExpression _getProperty(string propName)
    {
        if (_usingPropertyMapper)
        {
            if (_propertyMapper.TryGetValue(propName.ToLower(), out string newPropName))
                return Expression.Property(_parameter, newPropName.Pascalize());
            else
                throw new KeyNotFoundException($"You provided a property mapper and HotChocolate tried to use the property " +
                                               $"{propName.Pascalize()} but it was not found in the property mapper.");
        }
        else
            return Expression.Property(_parameter, propName.Pascalize());
    }

    public ConstantExpression _convertConstantType(ConstantExpression value, Type propType)
    {
        var valueTypeName = value.Type.Name;
        if (valueTypeName.Contains("Nullable"))
            valueTypeName = "Nullable" + Nullable.GetUnderlyingType(value.Type).Name;
        var propTypeName = propType.Name;
        if (propTypeName.Contains("Nullable"))
            propTypeName = "Nullable" + Nullable.GetUnderlyingType(propType).Name;

        return (valueTypeName, propTypeName) switch
        {
            (nameof(DateTimeOffset), "NullableDateTime") => Expression.Constant(((DateTimeOffset)value.Value).UtcDateTime, typeof(DateTime?)),
            (nameof(DateTimeOffset), nameof(DateTime)) => Expression.Constant(((DateTimeOffset)value.Value).UtcDateTime),
            (nameof(DateTimeOffset), "NullableDateTimeOffset") => Expression.Constant((DateTimeOffset)value.Value, typeof(DateTimeOffset?)),
            (nameof(DateTime), nameof(DateTimeOffset)) => Expression.Constant(new DateTimeOffset((DateTime)value.Value)),
            (nameof(DateTime), "NullableDateTimeOffset") => Expression.Constant(new DateTimeOffset((DateTime)value.Value), typeof(DateTimeOffset?)),
            (nameof(String), "NullableInt32") => Expression.Constant(int.TryParse((string)value.Value, out int intValue) ? intValue : 0, typeof(int?)),
            _ => throw new InvalidCastException($"No explicit conversion from {value.Type} to {propType} exists")
        };
    }

    /// <summary>
    /// Builds a <see cref="Connection"/> to return in your graphql endpoint to tell the middleware that paging is handled
    /// </summary>
    /// <typeparam name="THotChoc">The type HotChocolate is expecting</typeparam>
    /// <param name="collection">The collection of type <typeparamref name="THotChoc"/> to build a connection from</param>
    /// <returns>The <see cref="Connection"/> built from <paramref name="collection"/></returns>
    public Connection<THotChoc> BuildConnection<THotChoc>(IEnumerable<THotChoc> collection)
    {
        var edges = new List<Edge<THotChoc>>();
        for (var i = 0; i < collection.Count(); i++)
        {
            edges.Add(new Edge<THotChoc>(collection.ElementAt(i), ToBase64(_afterValue + i)));
        }
        return new Connection<THotChoc>(edges, _pageInfo, _totalCount);
    }

    /// <summary>
    /// Builds a <see cref="Connection"/> to return in your graphql endpoint to tell the middleware that paging is handled
    /// </summary>
    /// <returns>The <see cref="Connection"/> built from <see cref="Data"/></returns>
    public Connection<T> BuildConnection()
    {
        return BuildConnection(Data);
    }
}