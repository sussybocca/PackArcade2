console.log('Function started');
console.log('Arguments:', process.argv.slice(2));
const result = {
    message: 'Hello from Function!',
    timestamp: new Date().toISOString(),
    data: Array.from({ length: 10 }, (_, i) => ({ id: i, value: Math.random() * 100 }))
};
console.log('Function result:', JSON.stringify(result, null, 2));
return result;